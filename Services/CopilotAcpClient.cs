using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Client for the GitHub Copilot CLI using the Agent Client Protocol (ACP)
/// over NDJSON/stdio transport.
/// </summary>
public class CopilotAcpClient : IDisposable
{
    private Process? _proc;
    private int _nextId;
    private readonly Func<ProcessStartInfo, ProcessStartInfo> _startInfo;
    private string? _sessionId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly StringBuilder _promptText = new();
    private string? _modelConfigId;
    private List<CopilotModelInfo> _cachedModels = [];

    public CopilotAcpClient() : this(info => info) { }
    internal CopilotAcpClient(Func<ProcessStartInfo, ProcessStartInfo> startInfo) => _startInfo = startInfo;

    /// <summary>Path to the Copilot CLI executable.</summary>
    public string ExecPath { get; set; } = "copilot";

    /// <summary>Working directory for sessions.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Desired model id (empty = Copilot default).</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Callback invoked for each streamed text chunk.</summary>
    public Action<string>? OnChunk { get; set; }

    /// <summary>Callback invoked for each streamed thinking/reasoning chunk.</summary>
    public Action<string>? OnThinkingChunk { get; set; }

    /// <summary>Whether the process is alive.</summary>
    public bool IsAlive => _proc is { HasExited: false };

    /// <summary>Start the ACP process, initialize, and create a session.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsAlive && _sessionId != null) return;

        await StopAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _sessionId = null;

        var psi = new ProcessStartInfo(ExecPath, "--acp --stdio")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(WorkingDirectory))
            psi.WorkingDirectory = WorkingDirectory;

        _proc = new Process { StartInfo = _startInfo(psi) };
        _proc.OutputDataReceived += OnOutputData;
        _proc.ErrorDataReceived += OnErrorData;
        _proc.Exited += OnExited;
        _proc.EnableRaisingEvents = true;
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        // Initialize
        await RpcRequestAsync("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { },
            clientInfo = new
            {
                name = "novalist",
                title = "Novalist",
                version = "1.0.0",
            },
        }, cancellationToken);

        // Create session
        var sessionResult = await RpcRequestAsync("session/new", new
        {
            cwd = !string.IsNullOrEmpty(WorkingDirectory) ? WorkingDirectory : Environment.CurrentDirectory,
            mcpServers = Array.Empty<object>(),
        }, cancellationToken);

        AcceptSession(sessionResult);
    }

    private void AcceptSession(JsonElement sessionResult)
    {
        _sessionId = sessionResult.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("Copilot did not create a session.");
        ParseModels(sessionResult);

        // Discover config option for model selector
        _modelConfigId = null;
        if (sessionResult.TryGetProperty("configOptions", out var opts))
        {
            foreach (var opt in opts.EnumerateArray())
            {
                if (opt.TryGetProperty("category", out var cat) && cat.GetString() == "model" &&
                    opt.TryGetProperty("id", out var id))
                {
                    _modelConfigId = id.GetString();
                    break;
                }
            }
        }

    }

    /// <summary>Stop the ACP process gracefully.</summary>
    public async Task StopAsync()
    {
        if (_proc == null) return;
        var p = _proc;
        _proc = null;
        _sessionId = null;
        FailPending("ACP process stopped");

        try { p.StandardInput.Close(); } catch { /* ignore */ }

        // Kill may hang on a wedged process; isolate behind a Task with a hard cap.
        await Task.Run(() =>
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.WaitForExit(2000); } catch { /* ignore */ }
        }).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { /* swallow any timeout */ });

        try { p.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Reset the session to clear server-side conversation history.</summary>
    public async Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAlive || _sessionId == null) return;

        var sessionResult = await RpcRequestAsync("session/new", new
        {
            cwd = !string.IsNullOrEmpty(WorkingDirectory) ? WorkingDirectory : Environment.CurrentDirectory,
            mcpServers = Array.Empty<object>(),
        }, cancellationToken);

        AcceptSession(sessionResult);
    }

    /// <summary>Send cancel notification to abort current prompt.</summary>
    public void CancelPrompt()
    {
        if (_sessionId != null && _proc?.HasExited == false)
        {
            try { SendMessage(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = _sessionId } }); }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                FailPending("ACP process could not receive cancellation");
            }
        }
    }

    /// <summary>Send a text prompt and return the full response.</summary>
    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default, string? model = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(CancelPrompt);
        JsonElement result;
        try
        {
            if (!IsAlive || _sessionId == null)
                await StartAsync(cancellationToken);
            else
                await ResetSessionAsync(cancellationToken);

            var requestModel = model ?? ModelId;
            if (!string.IsNullOrEmpty(requestModel))
                await SelectModelAsync(requestModel, cancellationToken);
            _promptText.Clear();
            result = await RpcRequestAsync("session/prompt", new
            {
                sessionId = _sessionId,
                prompt = new[] { new { type = "text", text = prompt } },
            }, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync();
            throw new OperationCanceledException(cancellationToken);
        }

        var text = _promptText.ToString();
        _promptText.Clear();

        var stopReason = result.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
        if (stopReason != "end_turn")
            throw new InvalidOperationException($"Copilot stopped with reason: {stopReason}");

        return text;
    }

    /// <summary>Check whether the Copilot CLI is reachable.</summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(ExecPath, "--help")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            using var cts = new CancellationTokenSource(5000);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>List available models (cached from last session/new).</summary>
    public async Task<List<CopilotModelInfo>> ListModelsAsync()
    {
        if (!IsAlive || _sessionId == null)
            await StartAsync();
        return _cachedModels;
    }

    /// <summary>Apply model selection to the running session.</summary>
    public async Task ApplyModelAsync(string modelId)
    {
        if (!IsAlive || _sessionId == null) return;
        await SelectModelAsync(modelId);
    }

    // ── Internal helpers ─────────────────────────────────────────

    private async Task SelectModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        // Try session/set_config_option first (generic ACP)
        if (_modelConfigId != null)
        {
            try
            {
                await RpcRequestAsync("session/set_config_option", new
                {
                    sessionId = _sessionId,
                    configId = _modelConfigId,
                    value = modelId,
                }, cancellationToken);
                return;
            }
            catch (CopilotRpcException exception) when (exception.Code == -32601) { /* Older servers use session/set_model. */ }
        }

        // Try session/set_model (Copilot-specific)
        await RpcRequestAsync("session/set_model", new
        {
            sessionId = _sessionId,
            modelId,
        }, cancellationToken);
    }

    private void ParseModels(JsonElement result)
    {
        _cachedModels = [];
        if (result.TryGetProperty("models", out var models) &&
            models.TryGetProperty("availableModels", out var arr))
        {
            foreach (var m in arr.EnumerateArray())
            {
                var id = m.TryGetProperty("modelId", out var mid) ? mid.GetString() ?? "" : "";
                var name = m.TryGetProperty("name", out var mn) ? mn.GetString() ?? id : id;
                _cachedModels.Add(new CopilotModelInfo { Id = id, Name = name });
            }
        }
    }

    private void SendMessage(object msg)
    {
        var process = _proc;
        if (process?.HasExited != false) throw new InvalidOperationException("ACP process is not running.");
        var json = JsonSerializer.Serialize(msg);
        process.StandardInput.WriteLine(json);
        process.StandardInput.Flush();
    }

    private async Task<JsonElement> RpcRequestAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendMessage(new { jsonrpc = "2.0", id, method, @params = parameters });
            var timeout = method == "session/prompt" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(60);
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null || !ReferenceEquals(sender, _proc)) return;
        ProcessLine(e.Data);
    }

    private void ProcessLine(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement.Clone();
            HandleMessage(root);
        }
        catch { /* invalid JSON */ }
    }

    private void HandleMessage(JsonElement msg)
    {
        // Response to one of our requests
        if (msg.TryGetProperty("id", out var idProp))
        {
            var id = idProp.GetInt32();

            if (msg.TryGetProperty("error", out var err))
            {
                if (_pending.TryRemove(id, out var tcs))
                {
                    var message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "ACP error" : "ACP error";
                    var code = err.TryGetProperty("code", out var errorCode) ? errorCode.GetInt32() : 0;
                    tcs.TrySetException(new CopilotRpcException(code, message));
                }
                return;
            }

            if (msg.TryGetProperty("result", out var result))
            {
                if (_pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(result.Clone());
                return;
            }

            // Incoming request from agent (e.g. permission request)
            if (msg.TryGetProperty("method", out var method))
            {
                HandleIncomingRequest(id, method.GetString() ?? "", msg);
                return;
            }
        }

        // Notification (no id, has method)
        if (msg.TryGetProperty("method", out var notifMethod) && !msg.TryGetProperty("id", out _))
        {
            HandleNotification(notifMethod.GetString() ?? "", msg);
        }
    }

    private void HandleIncomingRequest(int id, string method, JsonElement msg)
    {
        if (method == "session/request_permission")
        {
            // Auto-reject all permission requests — we only want text generation
            JsonElement? rejectOptId = null;
            if (msg.TryGetProperty("params", out var p) &&
                p.TryGetProperty("options", out var options))
            {
                foreach (var opt in options.EnumerateArray())
                {
                    var kind = opt.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    if (kind is "reject_once" or "reject_always")
                    {
                        if (opt.TryGetProperty("optionId", out var oid))
                        {
                            rejectOptId = oid;
                            break;
                        }
                    }
                }
            }

            if (rejectOptId.HasValue)
            {
                SendMessage(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new { outcome = new { outcome = "selected", optionId = rejectOptId.Value.GetString() } }
                });
            }
            else
            {
                SendMessage(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new { outcome = new { outcome = "cancelled" } }
                });
            }
        }
        else
        {
            SendMessage(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code = -32601, message = "Method not supported" }
            });
        }
    }

    private void HandleNotification(string method, JsonElement msg)
    {
        if (method != "session/update") return;
        if (!msg.TryGetProperty("params", out var p) ||
            !p.TryGetProperty("update", out var update))
            return;
        if (!p.TryGetProperty("sessionId", out var session) || session.GetString() != _sessionId) return;

        var sessionUpdate = update.TryGetProperty("sessionUpdate", out var su) ? su.GetString() : null;

        if (sessionUpdate == "agent_message_chunk")
        {
            if (update.TryGetProperty("content", out var content) &&
                content.TryGetProperty("type", out var ct) && ct.GetString() == "text" &&
                content.TryGetProperty("text", out var text))
            {
                var t = text.GetString() ?? "";
                _promptText.Append(t);
                OnChunk?.Invoke(t);
            }
        }
        else if (sessionUpdate == "agent_thought_chunk")
        {
            if (update.TryGetProperty("content", out var content) &&
                content.TryGetProperty("type", out var ct) && ct.GetString() == "text" &&
                content.TryGetProperty("text", out var text))
            {
                OnThinkingChunk?.Invoke(text.GetString() ?? "");
            }
        }
    }

    private void OnErrorData(object sender, DataReceivedEventArgs e)
    {
        // stderr — ignore for now
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _proc)) return;
        _proc = null;
        _sessionId = null;
        FailPending("ACP process exited");
    }

    private void FailPending(string message)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetException(new InvalidOperationException(message));
        }
    }

    private sealed class CopilotRpcException(int code, string message) : InvalidOperationException(message)
    {
        public int Code { get; } = code;
    }

    public void Dispose()
    {
        // Fire-and-forget StopAsync to avoid blocking the calling thread
        // (Dispose may be invoked from the UI thread during teardown).
        _ = Task.Run(StopAsync);
        GC.SuppressFinalize(this);
    }
}

public class CopilotModelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

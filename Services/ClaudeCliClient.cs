using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Client for the Claude Code CLI (`claude`) in non-interactive print mode.
///
/// The point of this provider is speed without a bill: the CLI authenticates
/// with whatever the user already logged into — including a Claude Pro/Max
/// subscription — so a writer who has one gets hosted-model turnaround on the
/// initial analysis pass instead of waiting on a local model. Novalist never
/// holds a credential; this mirrors <see cref="CopilotAcpClient"/>, where the
/// user brings their own CLI and their own subscription.
///
/// One invocation is one prompt. The CLI is an agent harness rather than a
/// completion endpoint, so every call disables tools and pins the system
/// prompt to keep the result a plain answer to what we asked.
/// </summary>
public sealed class ClaudeCliClient
{
    /// <summary>
    /// The only environment the CLI is given.
    ///
    /// Novalist's backend inherits its environment from Electron, which inherits
    /// it from whatever terminal launched the app — so in a dev session that
    /// includes Electron, Vite, Chromium and VS Code debugger variables that a
    /// normal shell never has. Something in that set stops `claude -p` dead
    /// (it exits immediately with no output on any stream, while `claude
    /// --version` from the same place succeeds and the same command succeeds
    /// from a plain shell).
    ///
    /// Rather than guess which variable is responsible, the child is given a
    /// clean environment: the machine and user paths the CLI genuinely needs —
    /// PATH to resolve itself, USERPROFILE/APPDATA to find the logged-in
    /// credentials, TEMP to work in — and nothing else.
    /// </summary>
    private static readonly string[] AllowedEnvVars =
    [
        "PATH", "PATHEXT", "ComSpec", "SystemRoot", "SystemDrive", "windir",
        "TEMP", "TMP",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "USERNAME", "USERDOMAIN",
        "APPDATA", "LOCALAPPDATA", "ALLUSERSPROFILE", "ProgramData", "PUBLIC",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "COMPUTERNAME", "NUMBER_OF_PROCESSORS", "OS", "PROCESSOR_ARCHITECTURE",
        "SESSIONNAME", "LANG",
    ];

    /// <summary>Replaces the inherited environment with the allowlist above.</summary>
    private static void ApplyCleanEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach (var name in AllowedEnvVars)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) startInfo.Environment[name] = value;
        }
    }

    /// <summary>What `-p` carries when the real prompt is on stdin.</summary>
    private const string StdinDirective =
        "Respond to the input provided on standard input.";

    private Process? _process;
    private readonly object _processLock = new();

    /// <summary>Path to the Claude Code executable.</summary>
    public string ExecPath { get; set; } = "claude";

    /// <summary>Model alias or id (empty = whatever the CLI defaults to).</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Invoked with each chunk of answer text as it arrives.</summary>
    public Action<string>? OnChunk { get; set; }

    /// <summary>Invoked with each chunk of the model's reasoning as it arrives,
    /// so the UI can show thinking progress during a long scene.</summary>
    public Action<string>? OnThinkingChunk { get; set; }

    /// <summary>The model aliases the CLI accepts. There is no discovery
    /// endpoint — these are fixed by the CLI itself.</summary>
    public static IReadOnlyList<(string Key, string Name)> KnownModels =>
    [
        ("sonnet", "Claude Sonnet"),
        ("opus", "Claude Opus"),
        ("haiku", "Claude Haiku"),
        ("fable", "Claude Fable"),
    ];

    /// <summary>True when the executable exists and answers `--version`.</summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var (exitCode, _, _) = await RunAsync(["--version"], null, CancellationToken.None)
                .ConfigureAwait(false);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs one prompt and returns the model's text.
    /// </summary>
    public async Task<string> GenerateAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        // Direct spawn with a clean environment. `claude --version` already works
        // this way from the backend, so the spawn mechanism itself is sound; the
        // environment was the variable. The system prompt travels as a file so
        // the command line stays short enough for Windows even on a long scene.
        var systemFile = Path.Combine(Path.GetTempPath(), $"nl-claude-{Guid.NewGuid():N}.system.txt");
        var args = new List<string>
        {
            // The prompt travels on stdin, not as an argument: a scene plus the
            // Codex entity list runs to tens of thousands of characters, and
            // Windows caps a command line at ~32k ("The filename or extension is
            // too long"). stdin has no such limit.
            "-p", StdinDirective,
            // Newline-delimited events rather than one blob at the end, so the
            // model's reasoning and answer can be surfaced while it works.
            // --verbose and --include-partial-messages are what make the CLI emit
            // the per-token deltas rather than only whole messages.
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--disallowedTools", "*",
            "--permission-mode", "dontAsk",
        };

        int exitCode;
        string stdout, stderr;
        try
        {
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                await File.WriteAllTextAsync(systemFile, systemPrompt, cancellationToken).ConfigureAwait(false);
                args.Add("--system-prompt-file");
                args.Add(systemFile);
            }
            if (!string.IsNullOrWhiteSpace(ModelId))
            {
                args.Add("--model");
                args.Add(ModelId);
            }

            (exitCode, stdout, stderr) =
                await RunStreamingAsync(args, userPrompt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(systemFile)) File.Delete(systemFile); }
            catch (IOException) { /* a stray temp file is not worth failing over */ }
        }

        // A failed run reports through the JSON envelope on stdout, not stderr:
        // exit is non-zero, stderr is empty, and the reason (a bad model, an auth
        // problem, an API error) sits in `result` with `is_error: true`. Read
        // that before falling back to the exit code so the user sees the cause.
        var envelopeFailed = TryReadError(stdout, out var envelopeError);

        if (envelopeFailed || exitCode != 0)
        {
            // Dump the whole picture to the terminal the backend was launched
            // from — the failure often carries no message the UI can show, and on
            // an error stdout holds the CLI's error envelope, not story prose.
            LogFailure(exitCode, stdout, stderr);

            // When both streams are empty the process died before producing
            // anything — a launch/environment problem rather than an API error.
            // A `--version` probe tells us whether the binary can run here at all.
            if (!envelopeFailed && stdout.Trim().Length == 0 && stderr.Trim().Length == 0)
                await LogEnvironmentProbeAsync(cancellationToken).ConfigureAwait(false);

            if (envelopeFailed)
                throw new InvalidOperationException($"Claude CLI: {envelopeError}");

            var detail = stderr.Trim();
            if (detail.Length == 0) detail = stdout.Trim();
            throw new InvalidOperationException(detail.Length > 0
                ? $"Claude CLI exited with code {exitCode}: {Truncate(detail)}"
                : $"Claude CLI exited with code {exitCode} and produced no output "
                  + "(is the executable path correct and are you logged in?).");
        }

        // No OnChunk here: the streaming reader already forwarded the text as it
        // arrived, and emitting the whole answer again would duplicate it.
        return ExtractResult(stdout);
    }

    /// <summary>
    /// Runs the CLI in stream-json mode, forwarding reasoning and answer text as
    /// they arrive, and returns the final envelope so the existing error handling
    /// still applies. Each stdout line is one event; the last one is the `result`.
    /// </summary>
    private async Task<(int ExitCode, string StdOut, string StdErr)> RunStreamingAsync(
        IReadOnlyList<string> args, string? stdin, CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(args);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        lock (_processLock) _process = process;

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Feed stdin on its own task: a scene-sized prompt is far larger than
            // the pipe buffer, so writing it inline before reading stdout could
            // deadlock against a child that is already emitting events.
            var stdinTask = Task.Run(async () =>
            {
                if (stdin != null)
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                process.StandardInput.Close();
            }, cancellationToken);

            var resultLine = string.Empty;
            var answer = new StringBuilder();

            while (await process.StandardOutput.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                if (TryHandleStreamLine(line, answer, out var final)) resultLine = final;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // The `result` event carries the same envelope the non-streaming mode
            // returns, so downstream error handling is unchanged. Fall back to the
            // accumulated deltas if it never arrived.
            var stdout = resultLine.Length > 0
                ? resultLine
                : (answer.Length > 0 ? answer.ToString() : string.Empty);
            return (process.ExitCode, stdout, await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            throw;
        }
        finally
        {
            lock (_processLock) _process = null;
        }
    }

    /// <summary>Dispatches one stream event. Returns true when the line was the
    /// final `result` envelope, handing it back through <paramref name="final"/>.</summary>
    private bool TryHandleStreamLine(string line, StringBuilder answer, out string final)
    {
        final = string.Empty;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;   // not an event line; ignore rather than fail the run
        }
        if (root.ValueKind != JsonValueKind.Object) return false;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "result")
        {
            final = line;
            return true;
        }
        if (type != "stream_event"
            || !root.TryGetProperty("event", out var evt)
            || !evt.TryGetProperty("delta", out var delta))
            return false;

        switch (delta.TryGetProperty("type", out var dt) ? dt.GetString() : null)
        {
            case "thinking_delta" when delta.TryGetProperty("thinking", out var thinking):
                OnThinkingChunk?.Invoke(thinking.GetString() ?? string.Empty);
                break;
            case "text_delta" when delta.TryGetProperty("text", out var text):
                var chunk = text.GetString() ?? string.Empty;
                answer.Append(chunk);
                OnChunk?.Invoke(chunk);
                break;
        }
        return false;
    }

    /// <summary>Writes the raw failure to the terminal (and the debugger) so a
    /// launch or auth problem is visible even when the UI only shows a code.
    /// Runs only on failure, where stdout carries the CLI's error envelope
    /// rather than the model's answer.</summary>
    private static void LogFailure(int exitCode, string stdout, string stderr)
    {
        var report = $"[ClaudeCliClient] run failed: exit={exitCode}"
            + $"\n  stdout: {(stdout.Trim().Length == 0 ? "(empty)" : stdout.Trim())}"
            + $"\n  stderr: {(stderr.Trim().Length == 0 ? "(empty)" : stderr.Trim())}";
        Console.Error.WriteLine(report);
        Debug.WriteLine(report);
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";

    /// <summary>Runs `claude --version` and reports what the backend actually
    /// launches — the resolved path, working directory, and whether a bare probe
    /// even works — so a silent exit points at the real cause (wrong binary,
    /// missing PATH entry, unusable temp/working dir, poisoned inherited env).</summary>
    private async Task LogEnvironmentProbeAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[ClaudeCliClient] environment probe:");
        sb.AppendLine($"  ExecPath   = {ExecPath}");
        sb.AppendLine($"  WorkingDir = {Path.GetTempPath()}");
        // Names only — values can hold tokens, and the names alone are enough to
        // spot something the CLI reacts to that a working shell does not have.
        var names = Environment.GetEnvironmentVariables().Keys
            .Cast<object>().Select(k => k.ToString() ?? string.Empty)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        sb.AppendLine($"  EnvNames   = {string.Join(", ", names)}");
        try
        {
            var (code, versionOut, versionErr) =
                await RunAsync(["--version"], null, cancellationToken).ConfigureAwait(false);
            sb.AppendLine($"  --version  -> exit={code}, "
                + $"stdout={(versionOut.Trim().Length == 0 ? "(empty)" : versionOut.Trim())}, "
                + $"stderr={(versionErr.Trim().Length == 0 ? "(empty)" : versionErr.Trim())}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sb.AppendLine($"  --version  -> threw {ex.GetType().Name}: {ex.Message}");
        }

        // A minimal real `-p` run with --debug: --version can't reveal a
        // runtime-startup crash, but this shows the CLI's own diagnostics for
        // why an actual prompt dies. The prompt is a fixed literal, not story text.
        try
        {
            var (code, dbgOut, dbgErr) = await RunAsync(
                ["-p", "Reply with the single word OK.", "--output-format", "json",
                 "--disallowedTools", "*", "--permission-mode", "dontAsk", "--debug"],
                null, cancellationToken).ConfigureAwait(false);
            static string Cap(string s) => s.Length <= 2000 ? s : s[..2000] + "...";
            sb.AppendLine($"  -p --debug -> exit={code}");
            sb.AppendLine($"    stdout: {(dbgOut.Trim().Length == 0 ? "(empty)" : Cap(dbgOut.Trim()))}");
            sb.AppendLine($"    stderr: {(dbgErr.Trim().Length == 0 ? "(empty)" : Cap(dbgErr.Trim()))}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sb.AppendLine($"  -p --debug -> threw {ex.GetType().Name}: {ex.Message}");
        }

        // Run `-p` through a batch file (cmd /c). This does two things a direct
        // spawn can't: it gives the CLI a hidden console (cmd owns one even with
        // no window), and it captures output via OS-level file redirection rather
        // than our inherited pipes. A literal prompt keeps the batch quoting safe.
        // If this yields output where the direct spawn produced none, the CLI
        // needs a console — and this is how the real path must run.
        var batFile = Path.Combine(Path.GetTempPath(), "nl_claude_probe.cmd");
        var outFile = Path.Combine(Path.GetTempPath(), "nl_claude_probe_out.txt");
        var errFile = Path.Combine(Path.GetTempPath(), "nl_claude_probe_err.txt");
        try
        {
            await File.WriteAllTextAsync(batFile,
                $"@echo off\r\n\"{ExecPath}\" -p \"Reply with the single word OK.\" "
                + $"--output-format json --disallowedTools * --permission-mode dontAsk "
                + $"1> \"{outFile}\" 2> \"{errFile}\"\r\n",
                cancellationToken).ConfigureAwait(false);

            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(batFile);

            using var cmd = Process.Start(psi)!;
            await cmd.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string Read(string path) => File.Exists(path)
                ? (File.ReadAllText(path).Trim() is { Length: > 0 } t
                    ? (t.Length <= 2000 ? t : t[..2000] + "...") : "(empty)")
                : "(missing)";
            sb.AppendLine($"  cmd /c bat -> exit={cmd.ExitCode}");
            sb.AppendLine($"    file stdout: {Read(outFile)}");
            sb.AppendLine($"    file stderr: {Read(errFile)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sb.AppendLine($"  cmd /c bat -> threw {ex.GetType().Name}: {ex.Message}");
        }

        var report = sb.ToString().TrimEnd();
        Console.Error.WriteLine(report);
        Debug.WriteLine(report);
    }

    /// <summary>Stops the run in flight.</summary>
    public void CancelPrompt()
    {
        lock (_processLock)
        {
            try
            {
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone between the check and the kill.
            }
        }
    }

    /// <summary>
    /// Detects a failed run from the JSON envelope. Returns true and a
    /// human-readable reason when the CLI reported an error (`is_error: true`),
    /// pulling the message from `result` and appending the HTTP status when the
    /// failure was an API error. Non-JSON or a clean envelope returns false.
    /// </summary>
    internal static bool TryReadError(string stdout, out string message)
    {
        message = string.Empty;
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return false;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("is_error", out var isError)
                || isError.ValueKind != JsonValueKind.True)
                return false;

            var reason = root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.String
                ? result.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            if (root.TryGetProperty("api_error_status", out var status)
                && status.ValueKind == JsonValueKind.Number)
                reason = reason.Length > 0
                    ? $"{reason} (HTTP {status.GetInt32()})"
                    : $"API error (HTTP {status.GetInt32()})";

            message = reason.Length > 0 ? reason : "the run failed.";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pulls the answer out of the `--output-format json` envelope. The text
    /// lives in `result`; a schema-constrained run puts an object in
    /// `structured_output` instead. Anything unrecognised falls through as the
    /// raw output so a CLI change surfaces as odd text rather than silence.
    /// </summary>
    internal static string ExtractResult(string stdout)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return trimmed;

            if (root.TryGetProperty("structured_output", out var structured)
                && structured.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return structured.GetRawText();

            if (root.TryGetProperty("result", out var result))
            {
                return result.ValueKind == JsonValueKind.String
                    ? result.GetString() ?? string.Empty
                    : result.GetRawText();
            }

            return trimmed;
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    /// <summary>
    /// Runs the CLI once, feeding <paramref name="stdin"/> in and collecting both
    /// streams. Reads run concurrently with the wait so a large response cannot
    /// deadlock against a full pipe buffer.
    /// </summary>
    /// <summary>The start info both run paths share: redirected streams, no
    /// window, UTF-8, a neutral working directory, and the clean environment.</summary>
    private ProcessStartInfo BuildStartInfo(IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo(ExecPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // A neutral directory keeps project-level CLAUDE.md, MCP servers and
            // hooks out of the run. (`--bare` would skip those too, but it also
            // skips the keychain read that makes subscription auth work, so it
            // is not an option here.)
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        ApplyCleanEnvironment(startInfo);
        return startInfo;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        IReadOnlyList<string> args, string? stdin, CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(args);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        lock (_processLock) _process = process;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            if (stdin != null)
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            throw;
        }
        finally
        {
            lock (_processLock) _process = null;
        }
    }
}

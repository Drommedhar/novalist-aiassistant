using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed class AuditCliRequestTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CopilotCreatesIndependentSessionsAndIgnoresOldSessionChunks()
    {
        await using var cli = new FakeCli();
        var chunks = new ConcurrentQueue<string>();
        cli.Copilot.OnChunk = chunks.Enqueue;
        cli.Copilot.ModelId = "configured";
        Assert.Equal("first:one", await cli.Copilot.GenerateAsync("one", model: "first").WaitAsync(Deadline));
        Assert.Equal("second:two", await cli.Copilot.GenerateAsync("two", model: "second").WaitAsync(Deadline));
        Assert.Equal("first:onesecond:two", string.Concat(chunks));
        Assert.Equal("configured", cli.Copilot.ModelId);
        var selections = cli.Requests("session/set_config_option");
        Assert.Equal(2, selections.Count);
        Assert.Equal("config-1", selections[0].GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("config-2", selections[1].GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal(2, cli.Requests("session/new").Count);
    }

    [Fact]
    public async Task CopilotFallsBackOnlyWhenGenericModelSelectionIsUnsupported()
    {
        await using var cli = new FakeCli();
        cli.Signal("legacy-model");
        Assert.Equal("chosen:one", await cli.Copilot.GenerateAsync("one", model: "chosen").WaitAsync(Deadline));
        Assert.Single(cli.Requests("session/set_model"));
        cli.Signal("reject-model");
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            cli.Copilot.GenerateAsync("two", model: "unavailable").WaitAsync(Deadline));
        Assert.Contains("Model is unavailable", error.Message);
        Assert.Single(cli.Requests("session/prompt"));
        Assert.Single(cli.Requests("session/set_model"));
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("session/new")]
    [InlineData("session/set_config_option")]
    [InlineData("session/prompt")]
    public async Task CopilotCancellationStopsEveryProtocolPhaseAndCanRestart(string method)
    {
        await using var cli = new FakeCli();
        cli.Signal("hold-" + method.Replace('/', '-'));
        using var cancellation = new CancellationTokenSource();
        var generation = cli.Copilot.GenerateAsync("one", cancellation.Token, "chosen");
        await cli.WaitForRequestAsync(method);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation.WaitAsync(Deadline));
        Assert.False(cli.Copilot.IsAlive);
        Assert.Empty(cli.Requests("session/set_model"));
        if (method != "session/prompt") Assert.Empty(cli.Requests("session/prompt"));
        cli.Unsignal("hold-" + method.Replace('/', '-'));
        Assert.Equal("chosen:retry", await cli.Copilot.GenerateAsync("retry", model: "chosen").WaitAsync(Deadline));
    }

    [Fact]
    public async Task CopilotUnexpectedExitFailsPromptWithoutWaitingForRpcTimeout()
    {
        await using var cli = new FakeCli();
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            cli.Copilot.GenerateAsync("exit").WaitAsync(Deadline));
        Assert.Contains("exited", error.Message);
        Assert.Equal("default:retry", await cli.Copilot.GenerateAsync("retry").WaitAsync(Deadline));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task OverlappingCliRequestsKeepModelsAndCallbacksSeparate(string provider)
    {
        await using var cli = new FakeCli();
        var service = cli.Service(provider);
        var firstChunks = new ConcurrentQueue<string>();
        var secondChunks = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ModelOverride = "first";
        var first = service.GenerateChatAsync(Prompt("hold-first"), chunk =>
        {
            firstChunks.Enqueue(chunk);
            started.TrySetResult();
        });
        await started.Task.WaitAsync(Deadline);
        service.ModelOverride = "second";
        var second = service.GenerateChatAsync(Prompt("second"), secondChunks.Enqueue);
        Assert.False(second.IsCompleted);
        Assert.Empty(secondChunks);
        Assert.Single(cli.Requests(provider == "claude" ? "claude/prompt" : "session/prompt"));
        cli.Signal("release");
        Assert.Equal("first:hold-first", (await first.WaitAsync(Deadline)).Response);
        Assert.Equal("second:second", (await second.WaitAsync(Deadline)).Response);
        Assert.Equal("first:hold-first", string.Concat(firstChunks));
        Assert.Equal("second:second", string.Concat(secondChunks));
        Assert.Equal("configured", service.ModelName);
        Assert.Null(cli.Copilot.OnChunk);
        Assert.Null(cli.Claude.OnChunk);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task CancellingQueuedCliRequestPreservesActiveRequest(string provider)
    {
        await using var cli = new FakeCli();
        var service = cli.Service(provider);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.GenerateChatAsync(Prompt("hold-first"), _ => started.TrySetResult());
        await started.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var queued = service.GenerateChatAsync(Prompt("queued"), cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
        Assert.False(first.IsCompleted);
        cli.Signal("release");
        Assert.Equal("configured:hold-first", (await first.WaitAsync(Deadline)).Response);
        Assert.Single(cli.Requests(provider == "claude" ? "claude/prompt" : "session/prompt"));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task ServiceCancelStopsActiveAndQueuedCliRequestsAndReleasesGate(string provider)
    {
        await using var cli = new FakeCli();
        var service = cli.Service(provider);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = service.GenerateChatAsync(Prompt("hold-active"), _ => started.TrySetResult());
        await started.Task.WaitAsync(Deadline);
        var queued = service.GenerateChatAsync(Prompt("queued"));
        service.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
        Assert.Single(cli.Requests(provider == "claude" ? "claude/prompt" : "session/prompt"));
        Assert.Equal("configured:retry", (await service.GenerateChatAsync(Prompt("retry")).WaitAsync(Deadline)).Response);
        Assert.Null(cli.Copilot.OnChunk);
        Assert.Null(cli.Claude.OnChunk);
        service.Cancel();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaudeStreamsLongPromptsAndCleansSystemFilesEvenWhenCancelled(bool cancel)
    {
        await using var cli = new FakeCli();
        var prompt = (cancel ? "hold" : "answer") + new string('x', 160000);
        var system = new string('s', 70000);
        using var cancellation = new CancellationTokenSource();
        var generation = cli.Claude.GenerateAsync(system, prompt, cancellation.Token, "chosen");
        await cli.WaitForRequestAsync("claude/prompt");
        var request = Assert.Single(cli.Requests("claude/prompt"));
        Assert.Equal(prompt, request.GetProperty("prompt").GetString());
        Assert.Equal(system, request.GetProperty("system").GetString());
        var args = request.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()).ToArray();
        var systemFile = args[Array.IndexOf(args, "--system-prompt-file") + 1];
        Assert.NotNull(systemFile);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation.WaitAsync(Deadline));
        }
        else Assert.Equal("chosen:" + prompt, await generation.WaitAsync(Deadline));
        Assert.False(File.Exists(systemFile));
    }

    [Fact]
    public async Task PreCancelledCallsNeverLaunchAProcess()
    {
        await using var cli = new FakeCli();
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cli.Copilot.GenerateAsync("unused", cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cli.Claude.GenerateAsync("system", "unused", cancelled));
        Assert.Empty(cli.Requests());
    }

    private static List<AiChatMessage> Prompt(string content) => [new() { Role = "user", Content = content }];

    private sealed class FakeCli : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "novalist-fake-cli-" + Guid.NewGuid().ToString("N"));
        public CopilotAcpClient Copilot { get; }
        public ClaudeCliClient Claude { get; }

        public FakeCli()
        {
            Directory.CreateDirectory(_directory);
            Copilot = new CopilotAcpClient(info => Redirect(info, "copilot"));
            Claude = new ClaudeCliClient(info => Redirect(info, "claude"));
        }

        public AiService Service(string provider)
        {
            var service = new AiService(null, Copilot, Claude);
            service.Configure(new AiSettings { Provider = provider, CopilotModel = "configured", ClaudeModel = "configured" });
            return service;
        }

        public void Signal(string name) => File.WriteAllText(Path.Combine(_directory, name), "");
        public void Unsignal(string name) => File.Delete(Path.Combine(_directory, name));

        public List<JsonElement> Requests(string? method = null)
        {
            var transcript = Path.Combine(_directory, "requests.jsonl");
            if (!File.Exists(transcript)) return [];
            using var stream = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            var result = new List<JsonElement>();
            // A writer can still be appending the last line when this poll runs.
            foreach (var line in lines[..^1])
            {
                using var document = JsonDocument.Parse(line);
                if (method == null || document.RootElement.GetProperty("method").GetString() == method)
                    result.Add(document.RootElement.Clone());
            }
            return result;
        }

        public async Task WaitForRequestAsync(string method)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (Requests(method).Count == 0) await Task.Delay(10, deadline.Token);
        }

        private ProcessStartInfo Redirect(ProcessStartInfo info, string mode)
        {
            var originalArgs = info.ArgumentList.ToArray();
            info.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            info.Arguments = "";
            info.ArgumentList.Clear();
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "fake-cli", "Novalist.FakeAiCli.dll"));
            info.ArgumentList.Add(mode);
            info.ArgumentList.Add(_directory);
            foreach (var arg in originalArgs) info.ArgumentList.Add(arg);
            return info;
        }

        public async ValueTask DisposeAsync()
        {
            await Copilot.StopAsync();
            Claude.CancelPrompt();
            Directory.Delete(_directory, recursive: true);
        }
    }
}

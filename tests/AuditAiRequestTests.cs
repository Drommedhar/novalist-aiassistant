using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed class AuditAiRequestTests
{
    private static List<AiChatMessage> Prompt => [new() { Content = "Synthetic request" }];

    [Fact]
    public async Task EmptyUsageEventsAndTerminalEventsPreserveResponseAndTruncation()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Stream(
            "data: {\"choices\":[]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"length\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"total_tokens\":20}}\n\ndata: [DONE]\n"))));
        var service = Compatible(http);
        var result = await service.GenerateChatAsync(Prompt);
        Assert.Equal("answer", result.Response);
        Assert.True(result.WasTruncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingModelDiscoveryNeverStartsCompletion(bool cancelThroughService)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths = new ConcurrentBag<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable after cancellation");
        }));
        var service = new AiService(http);
        service.Configure(new AiSettings { Provider = "lmstudio", LmStudioModel = "local" });
        using var cancellation = new CancellationTokenSource();
        var generation = service.GenerateChatAsync(Prompt, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancelThroughService) service.Cancel(); else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("/api/v1/models", Assert.Single(paths));
        service.Cancel();
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task OverlappingRequestsRetainIndependentModelsAndCancellation(string provider)
    {
        var requests = new ConcurrentDictionary<string, CancellationToken>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            requests[document.RootElement.GetProperty("model").GetString()!] = token;
            if (requests.Count == 2) both.SetResult();
            await finish.Task.WaitAsync(token);
            return Stream(provider == "anthropic"
                ? "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}\n"
                : "data: {\"choices\":[{\"delta\":{\"content\":\"done\"}}]}\n");
        }));
        var service = new AiService(http);
        service.Configure(new AiSettings { Provider = provider, LmStudioModel = "default", AnthropicApiKey = "fake", AnthropicModel = "default" });
        using var firstCancellation = new CancellationTokenSource();
        service.ModelOverride = "first";
        var first = service.GenerateChatAsync(Prompt, cancellationToken: firstCancellation.Token);
        service.ModelOverride = "second";
        var second = service.GenerateChatAsync(Prompt);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(3));
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(requests["second"].IsCancellationRequested);
        finish.SetResult();
        Assert.Equal("done", (await second.WaitAsync(TimeSpan.FromSeconds(3))).Response);
        Assert.Equal("default", service.ModelName);
        service.Cancel();
    }

    [Fact]
    public void DuplicateHeadingsKeepTheirIndividualPreviewDisposition()
    {
        var engine = new ContextEngine { TokenBudget = 500 };
        var preview = engine.Preview([
            new(ContextTier.Subject, "Same heading", "kept"),
            new(ContextTier.Background, "Same heading", new string('x', 3000))]);
        Assert.True(preview.Lines[0].Kept);
        Assert.False(preview.Lines[1].Kept);
        Assert.Contains("kept", preview.Text);
    }

    [Fact]
    public void StreamingDeltasTransferOnlyNewTextAndResetCorrectly()
    {
        var stream = new ChatStreamUpdate();
        var source = new StringBuilder();
        var received = new StringBuilder();
        var transferred = 0;
        for (var n = 0; n < 3000; n++)
        {
            source.Append("word");
            var delta = stream.Next(source.ToString(), "reason");
            Assert.False(delta.Reset);
            received.Append(delta.StreamingDelta);
            transferred += delta.StreamingDelta.Length + delta.ThinkingDelta.Length;
        }
        Assert.Equal(source.ToString(), received.ToString());
        Assert.Equal(12006, transferred);
        var reset = stream.Next("", "");
        Assert.True(reset.Reset);
        Assert.Empty(reset.StreamingDelta);
        Assert.Equal("new", stream.Next("new", "").StreamingDelta);
    }

    internal static AiService Compatible(HttpClient http)
    {
        var service = new AiService(http);
        service.Configure(new AiSettings { Provider = "openai", LmStudioModel = "synthetic" });
        return service;
    }

    internal static HttpResponseMessage Stream(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/event-stream")
    };

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}

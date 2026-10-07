using System.Net;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed class AuditServiceCancellationTests
{
    [Fact]
    public async Task CancelAllUsesSnapshotWhenCancellationCallbackStartsAnotherRequest()
    {
        var started = 0;
        AiService? service = null;
        Task<AiChatResult>? retry = null;
        using var http = new HttpClient(new AuditAiRequestTests.Handler((_, token) =>
        {
            var response = new TaskCompletionSource<HttpResponseMessage>();
            var requestNumber = ++started;
            token.Register(() =>
            {
                if (requestNumber == 1)
                    retry = Assert.IsType<AiService>(service).GenerateChatAsync([new AiChatMessage { Content = "retry" }]);
                response.TrySetCanceled(token);
            });
            return response.Task;
        }));
        service = AuditAiRequestTests.Compatible(http);
        var first = service.GenerateChatAsync([new AiChatMessage { Content = "first" }]);
        var second = service.GenerateChatAsync([new AiChatMessage { Content = "second" }]);
        Assert.Equal(2, started);
        service.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(3, started);
        Assert.NotNull(retry);
        Assert.False(retry.IsCompleted);
        service.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task AnthropicDiscoveryUsesTheConfiguredTransportAndAuthentication()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new AuditAiRequestTests.Handler((request, _) =>
        {
            requests.Add(Assert.IsType<Uri>(request.RequestUri));
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"reachable\",\"display_name\":\"Reachable model\"}]}")
            });
        }));
        var client = new AnthropicClient(http) { ApiKey = "test-key", BaseUrl = "https://test.invalid" };
        Assert.True(await client.IsAvailableAsync());
        var model = Assert.Single(await client.ListModelsAsync());
        Assert.Equal("reachable", model.Key);
        Assert.Equal("Reachable model", model.DisplayName);
        Assert.Equal(new[] { "/v1/models", "/v1/models?limit=100" }, requests.Select(uri => uri.PathAndQuery));
    }
}

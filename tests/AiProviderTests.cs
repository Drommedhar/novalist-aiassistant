using System.Net;
using System.Text;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Novalist.Sdk.Models.Wizards;
using Novalist.Sdk.Services;
using Xunit;

public class AiProviderTests
{
    [Fact]
    public void SelectingOllamaSetsUrlAndClearsThePreviousServicesModelAndKey()
    {
        var settings = new AiSettings { LmStudioModel = "old-model", LmStudioApiToken = "old-key" };
        AiProviders.ApplyConnection(settings, new Dictionary<string, string> { ["provider"] = "ollama" });
        Assert.Equal("ollama", settings.Provider);
        Assert.Equal("http://localhost:11434/v1", settings.LmStudioBaseUrl);
        Assert.Empty(settings.LmStudioModel);
        Assert.Empty(settings.LmStudioApiToken);

        AiProviders.ApplyConnection(settings, new Dictionary<string, string> { ["lmStudioBaseUrl"] = "http://my-server:11434/v1" });
        AiProviders.ApplyConnection(settings, new Dictionary<string, string> { ["provider"] = "ollama" });
        Assert.Equal("http://my-server:11434/v1", settings.LmStudioBaseUrl);
    }

    [Theory]
    [InlineData("ollama", "http://my-server:11434/v1", "ollama")]
    [InlineData("", "http://localhost:11434/v1", "ollama")]
    [InlineData("", "http://my-lmstudio:1234", "lmstudio")]
    public void LegacySettingsKeepTheirEndpoint(string preset, string url, string expected)
    {
        var settings = new AiSettings { OpenAiCompatiblePreset = preset, LmStudioBaseUrl = url, LmStudioApiToken = "key" };
        Assert.Equal(expected, AiProviders.Selected(settings));
        AiProviders.ApplyConnection(settings, new Dictionary<string, string>());
        Assert.Equal(expected, settings.Provider);
        Assert.Equal(url, settings.LmStudioBaseUrl);
        Assert.Equal("key", settings.LmStudioApiToken);
        Assert.Empty(settings.OpenAiCompatiblePreset);
    }

    [Fact]
    public void ProviderChangeKeepsExplicitConnectionEdits()
    {
        var settings = new AiSettings();
        AiProviders.ApplyConnection(settings, new Dictionary<string, string>
        {
            ["provider"] = "ollama", ["lmStudioBaseUrl"] = "http://remote:11434/v1",
            ["lmStudioModel"] = "story-model", ["lmStudioApiToken"] = "proxy-key"
        });
        Assert.Equal("http://remote:11434/v1", settings.LmStudioBaseUrl);
        Assert.Equal("story-model", settings.LmStudioModel);
        Assert.Equal("proxy-key", settings.LmStudioApiToken);
    }

    [Theory]
    [InlineData("ollama", "http://localhost:11434/v1/", "/v1")]
    [InlineData("ollama", "http://localhost:11434", "/v1")]
    [InlineData("openrouter", "https://openrouter.ai/api/v1", "/api/v1")]
    [InlineData("custom", "https://example.test/gateway/v1/", "/gateway/v1")]
    public async Task CompatibleServicesDiscoverModelsAndStreamChatWithoutLmStudioProbes(string provider, string url, string apiPath)
    {
        var requests = new List<string>();
        using var handler = new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(apiPath + "/models", path);
                return Json("""{"data":[{"id":"story-model"}]}""");
            }
            Assert.Equal(apiPath + "/chat/completions", path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("story-model", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.TryGetProperty("min_p", out _));
            Assert.False(body.RootElement.TryGetProperty("repeat_last_n", out _));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });
        using var http = new HttpClient(handler);
        var service = new AiService(http);
        service.Configure(new AiSettings { Provider = provider, LmStudioBaseUrl = url, LmStudioModel = "story-model", LmStudioApiToken = "test-key" });
        Assert.True(await service.IsServerRunningAsync());
        Assert.Equal("story-model", Assert.Single(await service.ListModelsAsync()).Key);
        var chunks = new StringBuilder();
        await service.GenerateChatAsync([new AiChatMessage { Role = "user", Content = "Hello" }], chunk => chunks.Append(chunk));
        Assert.Equal("Hello", chunks.ToString());
        Assert.Equal(new[] { apiPath + "/models", apiPath + "/models", apiPath + "/chat/completions" }, requests);
    }

    [Fact]
    public async Task LmStudioRetainsNativeModelDiscoveryAndLoadChecks()
    {
        var paths = new List<string>();
        using var handler = new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"models":[{"type":"llm","key":"local-model","display_name":"Local","loaded_instances":[{"id":"instance"}]}]}"""));
        });
        using var http = new HttpClient(handler);
        var service = new AiService(http);
        service.Configure(new AiSettings { LmStudioBaseUrl = "http://localhost:1234/v1", LmStudioModel = "local-model" });
        Assert.Equal("local-model", Assert.Single(await service.ListModelsAsync()).Key);
        await service.EnsureModelLoadedAsync();
        Assert.Equal(new[] { "/api/v1/models", "/api/v1/models" }, paths);
    }

    [Fact]
    public void SetupOffersOllamaAndUsesItsOwnDefaultsWhenSwitchingFromLmStudio()
    {
        var wizard = AiSetupWizard.Build();
        var providers = Assert.IsType<ChoiceStep>(wizard.Steps.Single(s => s.Id == "provider"));
        Assert.Contains(providers.Choices, c => c.Value == "ollama" && c.Label == "Ollama");
        var settings = new AiSettings { LmStudioModel = "lm-model", LmStudioApiToken = "lm-key" };
        var answers = AiSetupWizard.CreateSeed(settings);
        answers.Answers["enabled"] = new WizardAnswer { Text = "true" };
        answers.Answers["provider"] = new WizardAnswer { Text = "ollama" };
        answers.Answers["ollamaModel"] = new WizardAnswer { Text = "ollama-model" };
        AiSetupWizard.Apply(settings, answers);
        Assert.Equal("ollama", settings.Provider);
        Assert.Equal("http://localhost:11434/v1", settings.LmStudioBaseUrl);
        Assert.Equal("ollama-model", settings.LmStudioModel);
        Assert.Empty(settings.LmStudioApiToken);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

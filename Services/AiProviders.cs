using Novalist.Sdk.Models;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>The services shown to writers, independent of their wire protocol.</summary>
public static class AiProviders
{
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["lmstudio"] = "LM Studio",
        ["ollama"] = "Ollama",
        ["openai"] = "OpenAI",
        ["openrouter"] = "OpenRouter",
        ["groq"] = "Groq",
        ["deepseek"] = "DeepSeek",
        ["mistral"] = "Mistral",
        ["together"] = "Together AI",
        ["xai"] = "xAI",
        ["custom"] = "Custom (OpenAI-compatible)",
        ["anthropic"] = "Anthropic API",
        ["copilot"] = "GitHub Copilot CLI",
        ["claude"] = "Claude Code CLI",
    };

    public static string[] CompatibleIds { get; } = [.. AiSettings.OpenAiCompatiblePresets.Keys, "custom"];

    public static bool IsCompatible(string provider) => CompatibleIds.Contains(provider);

    // Old versions represented every compatible service as "lmstudio" plus a
    // preset. Keep those connections working without replacing a custom URL.
    public static string Selected(AiSettings settings)
    {
        if (settings.Provider != "lmstudio") return settings.Provider;
        if (AiSettings.BaseUrlForPreset(settings.OpenAiCompatiblePreset) != null)
            return settings.OpenAiCompatiblePreset.ToLowerInvariant();
        return AiSettings.OpenAiCompatiblePresets
            .FirstOrDefault(p => p.Value.TrimEnd('/').Equals(settings.LmStudioBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            .Key ?? "lmstudio";
    }

    public static string ApiRoot(string baseUrl)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        // A bare server address is convenient for local servers. Addresses
        // with a path already name the API root, including /v1 and /api/v1.
        return Uri.TryCreate(root, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/"
            ? root + "/v1" : root;
    }

    public static string LmStudioRoot(string baseUrl)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root[..^3] : root;
    }

    public static void ApplyConnection(AiSettings settings, IReadOnlyDictionary<string, string> values)
    {
        string Read(string key, string current) => values.TryGetValue(key, out var value) ? value : current;
        var previous = Selected(settings);
        var provider = Read("provider", previous);
        if (!Labels.ContainsKey(provider)) provider = previous;
        var changed = provider != previous;
        var url = Read("lmStudioBaseUrl", settings.LmStudioBaseUrl);
        var model = Read("lmStudioModel", settings.LmStudioModel);
        var token = Read("lmStudioApiToken", settings.LmStudioApiToken);
        if (changed && IsCompatible(provider))
        {
            if (url == settings.LmStudioBaseUrl)
                url = AiSettings.BaseUrlForPreset(provider) ?? url;
            // Models and credentials belong to the previous service. Keep
            // explicit edits made alongside the selection, not stale values.
            if (model == settings.LmStudioModel) model = string.Empty;
            if (token == settings.LmStudioApiToken) token = string.Empty;
        }
        settings.Provider = provider;
        settings.OpenAiCompatiblePreset = string.Empty;
        settings.LmStudioBaseUrl = url;
        settings.LmStudioModel = model;
        settings.LmStudioApiToken = token;
        settings.CopilotPath = Read("copilotPath", settings.CopilotPath);
        settings.CopilotModel = Read("copilotModel", settings.CopilotModel);
        settings.ClaudePath = Read("claudePath", settings.ClaudePath);
        settings.ClaudeModel = Read("claudeModel", settings.ClaudeModel);
        settings.AnthropicApiKey = Read("anthropicApiKey", settings.AnthropicApiKey);
        settings.AnthropicModel = Read("anthropicModel", settings.AnthropicModel);
        settings.AnthropicBaseUrl = Read("anthropicBaseUrl", settings.AnthropicBaseUrl);
    }
}

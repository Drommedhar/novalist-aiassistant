using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Models.Wizards;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// One-shot setup wizard that asks the user to choose an AI provider and
/// configure the minimum settings needed to make AI features work
/// (provider, model, base URL or path, API token, response language).
/// </summary>
public static class AiSetupWizard
{
    public const string Id = "extension.ai.setup";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static WizardDefinition Build(Func<string, string>? loc = null)
    {
        string T(string key, string fallback) => loc?.Invoke(key) is { } v && v != key ? v : fallback;

        return new WizardDefinition
        {
            Id = Id,
            DisplayName = T("wizard.ai.displayName", "AI Assistant — setup"),
            Description = T("wizard.ai.description", "Pick a provider and fill in just enough to start chatting."),
            Scope = WizardScope.Reference,
            Steps =
            [
                new ChoiceStep
                {
                    Id = "enabled",
                    Title = T("wizard.ai.enabled.title", "Enable AI features?"),
                    Help = T("wizard.ai.enabled.help", "Master toggle. You can flip this off later in Settings → AI."),
                    Skippable = false,
                    Choices =
                    [
                        new WizardChoice { Value = "true", Label = T("wizard.ai.enabled.yes", "Yes — set it up now") },
                        new WizardChoice { Value = "false", Label = T("wizard.ai.enabled.no", "Not now") },
                    ],
                },
                new ChoiceStep
                {
                    Id = "provider",
                    Title = T("wizard.ai.provider.title", "Which provider?"),
                    Help = T("wizard.ai.provider.help", "Choose your local AI server, hosted service, or command-line tool."),
                    Skippable = false,
                    VisibleWhen = new WizardCondition { StepId = "enabled", Operator = "equals", Value = "true" },
                    Choices = AiProviders.Labels.Select(p => new WizardChoice
                    {
                        Value = p.Key,
                        Label = p.Key == "custom" ? T("settings.aiCustomProvider", p.Value) : p.Value,
                    }).ToList(),
                },

                .. CompatibleSteps(loc),

                new TextStep
                {
                    Id = "anthropicBaseUrl",
                    Title = T("settings.aiAnthropicBaseUrl", "Anthropic API address"),
                    Placeholder = "https://api.anthropic.com",
                    VisibleWhen = new WizardCondition { StepId = "provider", Value = "anthropic" },
                },
                new TextStep
                {
                    Id = "anthropicApiKey",
                    Title = T("settings.aiAnthropicKey", "Anthropic API key"),
                    Skippable = false,
                    VisibleWhen = new WizardCondition { StepId = "provider", Value = "anthropic" },
                },
                new TextStep
                {
                    Id = "anthropicModel",
                    Title = T("settings.aiAnthropicModel", "Anthropic model"),
                    VisibleWhen = new WizardCondition { StepId = "provider", Value = "anthropic" },
                },

                new TextStep
                {
                    Id = "copilotPath",
                    Title = T("wizard.ai.copilotPath.title", "Copilot CLI path"),
                    Help = T("wizard.ai.copilotPath.help", "Path or command name. \"copilot\" works if the binary is on PATH."),
                    Placeholder = "copilot",
                    VisibleWhen = new WizardCondition { StepId = "provider", Operator = "equals", Value = "copilot" },
                },
                new TextStep
                {
                    Id = "copilotModel",
                    Title = T("wizard.ai.copilotModel.title", "Copilot model (optional)"),
                    Help = T("wizard.ai.copilotModel.help", "Empty uses the CLI's default model."),
                    VisibleWhen = new WizardCondition { StepId = "provider", Operator = "equals", Value = "copilot" },
                },

                new TextStep
                {
                    Id = "claudePath",
                    Title = T("wizard.ai.claudePath.title", "Claude Code CLI path"),
                    Help = T("wizard.ai.claudePath.help", "Path or command name. \"claude\" works if the binary is on PATH."),
                    Placeholder = "claude",
                    VisibleWhen = new WizardCondition { StepId = "provider", Operator = "equals", Value = "claude" },
                },
                new TextStep
                {
                    Id = "claudeModel",
                    Title = T("wizard.ai.claudeModel.title", "Claude model"),
                    Help = T("wizard.ai.claudeModel.help", "An alias the CLI accepts: sonnet, opus, haiku, or fable."),
                    Placeholder = "sonnet",
                    VisibleWhen = new WizardCondition { StepId = "provider", Operator = "equals", Value = "claude" },
                },

                new TextStep
                {
                    Id = "responseLanguage",
                    Title = T("wizard.ai.responseLanguage.title", "Response language"),
                    Help = T("wizard.ai.responseLanguage.help", "Empty = follow the app's UI language."),
                    Placeholder = T("wizard.ai.responseLanguage.placeholder", "English"),
                    VisibleWhen = new WizardCondition { StepId = "enabled", Operator = "equals", Value = "true" },
                },
            ],
        };
    }

    /// <summary>Maps the wizard's answers onto the AiSettings record.</summary>
    public static bool Apply(AiSettings settings, WizardResult result)
    {
        var enabled = string.Equals(result.GetText("enabled"), "true", System.StringComparison.OrdinalIgnoreCase);
        settings.Enabled = enabled;
        if (!enabled) return true;

        var provider = result.GetText("provider") ?? AiProviders.Selected(settings);
        var values = new Dictionary<string, string> { ["provider"] = provider };
        if (AiProviders.IsCompatible(provider))
        {
            var url = result.GetText(FieldId(provider, "BaseUrl"));
            values["lmStudioBaseUrl"] = !string.IsNullOrWhiteSpace(url) ? url
                : provider == AiProviders.Selected(settings) ? settings.LmStudioBaseUrl
                : AiSettings.BaseUrlForPreset(provider) ?? settings.LmStudioBaseUrl;
            values["lmStudioModel"] = result.GetText(FieldId(provider, "Model")) ?? string.Empty;
            values["lmStudioApiToken"] = result.GetText(FieldId(provider, "ApiToken")) ?? string.Empty;
        }
        else
        {
            foreach (var key in new[] { "copilotPath", "copilotModel", "claudePath", "claudeModel",
                "anthropicBaseUrl", "anthropicApiKey", "anthropicModel" })
            {
                var value = result.GetText(key);
                if (value != null) values[key] = value;
            }
        }
        AiProviders.ApplyConnection(settings, values);

        var lang = result.GetText("responseLanguage");
        settings.ResponseLanguage = lang ?? string.Empty;
        return true;
    }

    private static string FieldId(string provider, string suffix) =>
        (provider == "lmstudio" ? "lmStudio" : provider) + suffix;

    public static WizardResult CreateSeed(AiSettings settings)
    {
        var provider = AiProviders.Selected(settings);
        var seed = new WizardResult { DefinitionId = Id };
        void Set(string key, string value) => seed.Answers[key] = new WizardAnswer { Text = value };
        Set("enabled", settings.Enabled ? "true" : "false");
        Set("provider", provider);
        foreach (var id in AiProviders.CompatibleIds)
        {
            Set(FieldId(id, "BaseUrl"), id == provider ? settings.LmStudioBaseUrl : AiSettings.BaseUrlForPreset(id) ?? "");
            if (id != provider) continue;
            Set(FieldId(id, "Model"), settings.LmStudioModel);
            Set(FieldId(id, "ApiToken"), settings.LmStudioApiToken);
        }
        Set("copilotPath", settings.CopilotPath);
        Set("copilotModel", settings.CopilotModel);
        Set("claudePath", settings.ClaudePath);
        Set("claudeModel", settings.ClaudeModel);
        Set("anthropicBaseUrl", settings.AnthropicBaseUrl);
        Set("anthropicApiKey", settings.AnthropicApiKey);
        Set("anthropicModel", settings.AnthropicModel);
        Set("responseLanguage", settings.ResponseLanguage);
        return seed;
    }

    private static IEnumerable<WizardStep> CompatibleSteps(Func<string, string>? loc)
    {
        string T(string key, string fallback) => loc?.Invoke(key) is { } v && v != key ? v : fallback;
        foreach (var provider in AiProviders.CompatibleIds)
        {
            var urlId = FieldId(provider, "BaseUrl");
            var tokenId = FieldId(provider, "ApiToken");
            var condition = new WizardCondition { StepId = "provider", Value = provider };
            string Url(WizardResult result) => string.IsNullOrWhiteSpace(result.GetText(urlId))
                ? AiSettings.BaseUrlForPreset(provider) ?? "" : result.GetText(urlId)!;
            yield return new TextStep
            {
                Id = urlId,
                Title = T("settings.aiBaseUrl", "Base URL"),
                Help = T("wizard.ai.endpoint.help", "Use the default address or enter the address of your server."),
                Placeholder = AiSettings.BaseUrlForPreset(provider),
                Skippable = provider != "custom",
                VisibleWhen = condition,
            };
            yield return new TextStep
            {
                Id = tokenId,
                Title = T("settings.aiApiToken", "API token"),
                Help = T("settings.aiApiTokenDesc", "API key for the selected service. Local servers usually do not require one."),
                VisibleWhen = condition,
                Validator = r => ValidateEndpointAsync(provider, Url(r), r.GetText(tokenId), loc),
            };
            yield return new ChoiceStep
            {
                Id = FieldId(provider, "Model"),
                Title = T("settings.aiModel", "Model"),
                Help = T("wizard.ai.endpoint.modelHelp", "Choose a model available from the selected service."),
                VisibleWhen = condition,
                AutoSkipIfChoicesEmpty = true,
                DynamicChoicesProvider = r => FetchModelsAsync(provider, Url(r), r.GetText(tokenId)),
            };
        }
    }

    public static async Task<string?> ValidateEndpointAsync(
        string provider, string url, string? token = null, Func<string, string>? loc = null)
    {
        string T(string key, string fallback) => loc?.Invoke(key) is { } v && v != key ? v : fallback;
        var endpoint = provider == "lmstudio"
            ? $"{AiProviders.LmStudioRoot(url)}/api/v1/models"
            : $"{AiProviders.ApiRoot(url)}/models";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            return res.IsSuccessStatusCode ? null : string.Format(
                T("wizard.ai.endpoint.errorStatus", "The server responded {0} {1}. Check the address and API key."),
                (int)res.StatusCode, res.ReasonPhrase);
        }
        catch (Exception ex)
        {
            return string.Format(T("wizard.ai.lmStudioUrl.errorReach", "Cannot reach {0}: {1}"), url, ex.Message);
        }
    }

    public static async Task<IReadOnlyList<WizardChoice>> FetchModelsAsync(string provider, string url, string? token = null)
    {
        var service = new AiService(_http);
        service.Configure(new AiSettings { Provider = provider, LmStudioBaseUrl = url, LmStudioApiToken = token ?? "" });
        return (await service.ListModelsAsync().ConfigureAwait(false))
            .Select(m => new WizardChoice { Value = m.Key, Label = string.IsNullOrEmpty(m.DisplayName) ? m.Key : m.DisplayName })
            .ToList();
    }
}

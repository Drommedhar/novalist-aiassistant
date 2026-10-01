using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Models.Wizards;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// One-shot setup for independently enabled AI assistance and local dictation.
/// </summary>
public static class AiSetupWizard
{
    public const string Id = "extension.ai.setup";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static bool ShouldOffer(AiSettings settings, bool hasSavedDictationChoice = false)
        => !settings.SetupCompleted && !settings.Enabled
            && !(hasSavedDictationChoice && settings.DictationEnabled)
            && string.IsNullOrWhiteSpace(settings.LmStudioModel)
            && string.IsNullOrWhiteSpace(settings.CopilotModel)
            && string.IsNullOrWhiteSpace(settings.AnthropicApiKey);

    public static WizardDefinition Build(Func<string, string>? loc = null)
    {
        string T(string key, string fallback) => loc?.Invoke(key) is { } v && v != key ? v : fallback;

        return new WizardDefinition
        {
            Id = Id,
            DisplayName = T("wizard.ai.displayName", "AI Assistant — setup"),
            Description = T("wizard.ai.description", "Set up AI assistance, local dictation, or both."),
            Scope = WizardScope.Reference,
            Steps =
            [
                new ChoiceStep
                {
                    Id = "features",
                    Title = T("wizard.ai.features.title", "Which features would you like?"),
                    Help = T("wizard.ai.features.help", "AI assistance and dictation work independently. You can change this later in Settings → AI Assistant."),
                    Skippable = false,
                    Choices =
                    [
                        new WizardChoice { Value = "ai", Label = T("wizard.ai.features.ai", "AI assistance only") },
                        new WizardChoice { Value = "dictation", Label = T("wizard.ai.features.dictation", "Local dictation only") },
                        new WizardChoice { Value = "ai,dictation", Label = T("wizard.ai.features.both", "Both") },
                        new WizardChoice { Value = "none", Label = T("wizard.ai.enabled.no", "Not now") },
                    ],
                },
                new ChoiceStep
                {
                    Id = "provider",
                    Title = T("wizard.ai.provider.title", "Which provider?"),
                    Help = T("wizard.ai.provider.help", "Choose your local AI server, hosted service, or command-line tool."),
                    Skippable = false,
                    VisibleWhen = new WizardCondition { StepId = "features", Operator = "contains", Value = "ai" },
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
                    VisibleWhen = new WizardCondition { StepId = "features", Operator = "contains", Value = "ai" },
                },
                new ChoiceStep
                {
                    Id = "dictationAcceleration", Title = T("dictation.acceleration", "Acceleration"),
                    Help = T("dictation.accelerationHelp", "Choose Automatic or the accelerator for this computer."),
                    Skippable = false,
                    VisibleWhen = new WizardCondition { StepId = "features", Operator = "contains", Value = "dictation" },
                    Choices = DictationHardware.Choices.Select(value => new WizardChoice
                    {
                        Value = value, Label = value == "auto" ? T("dictation.automatic", "Automatic") : value.ToUpperInvariant()
                    }).ToList(),
                },
                new ChoiceStep
                {
                    Id = "dictationModel", Title = T("dictation.model", "Speech recognition model"),
                    Help = T("dictation.help", "English and German dictation runs locally and independently of the chat provider."),
                    Skippable = false,
                    VisibleWhen = new WizardCondition { StepId = "features", Operator = "contains", Value = "dictation" },
                    Choices = new[] { ("base", "Whisper Base (~150–300 MB)"), ("small", "Whisper Small (~500 MB–1 GB)"),
                        ("medium", "Whisper Medium (~1.5–3.1 GB)"), ("large-v3", "Whisper Large v3 (~3.1 GB)") }
                        .Select(p => new WizardChoice { Value = p.Item1, Label = p.Item2 }).ToList(),
                },
                new ChoiceStep
                {
                    Id = "dictationDialogueModel", Title = T("dictation.dialogueModel", "Dialogue detection model"),
                    Skippable = false,
                    VisibleWhen = new WizardCondition { StepId = "features", Operator = "contains", Value = "dictation" },
                    Choices = [new() { Value = "1.7B", Label = "Qwen3 1.7B (~3.5 GB)" },
                        new() { Value = "4B", Label = "Qwen3 4B (~8 GB)" }],
                },
                new ChoiceStep
                {
                    Id = "prepareDictation", Title = T("dictation.prepare", "Download / repair dictation models"),
                    Help = T("wizard.ai.download.help", "Download and check the selected models after finishing setup. Existing models are reused. Internet and additional disk space are needed for setup; dictation then works offline."),
                    Skippable = false,
                    VisibleWhen = new WizardCondition { StepId = "features", Operator = "contains", Value = "dictation" },
                    Choices = [new() { Value = "true", Label = T("wizard.ai.download.now", "Download after setup") },
                        new() { Value = "false", Label = T("wizard.ai.download.later", "Download later in settings") }],
                },
            ],
        };
    }

    /// <summary>Maps the wizard's answers onto the AiSettings record.</summary>
    public static bool Apply(AiSettings settings, WizardResult result)
    {
        if (!result.Completed) return false;
        var features = result.GetText("features");
        if (features is not ("ai" or "dictation" or "ai,dictation" or "none")) return false;
        var dictation = features is "dictation" or "ai,dictation";
        if (dictation)
        {
            var speech = result.GetText("dictationModel");
            var dialogue = result.GetText("dictationDialogueModel");
            var acceleration = result.GetText("dictationAcceleration");
            LocalDictationRuntime.ValidateModels(speech, dialogue);
            if (!DictationHardware.Choices.Contains(acceleration)) throw new ArgumentException("Choose a supported dictation accelerator.");
            settings.DictationModel = speech;
            settings.DictationDialogueModel = dialogue;
            settings.DictationAcceleration = acceleration;
        }
        settings.Enabled = features is "ai" or "ai,dictation";
        settings.DictationEnabled = dictation;
        settings.SetupCompleted = true;
        if (!settings.Enabled) return true;

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
        Set("features", (settings.Enabled, settings.DictationEnabled) switch
        {
            (true, true) => "ai,dictation", (true, false) => "ai", (false, true) => "dictation", _ => "none"
        });
        Set("dictationModel", settings.DictationModel);
        Set("dictationDialogueModel", settings.DictationDialogueModel);
        Set("dictationAcceleration", settings.DictationAcceleration);
        Set("prepareDictation", "true");
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

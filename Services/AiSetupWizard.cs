using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
                    Help = T("wizard.ai.provider.help", "LM Studio runs a model locally. GitHub Copilot CLI is cloud-hosted and needs a copilot binary on PATH."),
                    Skippable = false,
                    VisibleWhen = new WizardCondition { StepId = "enabled", Operator = "equals", Value = "true" },
                    Choices =
                    [
                        new WizardChoice
                        {
                            Value = "lmstudio",
                            Label = T("wizard.ai.provider.lmstudio", "LM Studio (local)"),
                            Description = T("wizard.ai.provider.lmstudioDesc", "Free, private, runs against a model loaded in LM Studio on your machine."),
                        },
                        new WizardChoice
                        {
                            Value = "copilot",
                            Label = T("wizard.ai.provider.copilot", "GitHub Copilot CLI"),
                            Description = T("wizard.ai.provider.copilotDesc", "Uses the copilot binary on PATH. Requires a Copilot subscription."),
                        },
                    ],
                },

                new TextStep
                {
                    Id = "lmStudioBaseUrl",
                    Title = T("wizard.ai.lmStudioUrl.title", "LM Studio base URL"),
                    Help = T("wizard.ai.lmStudioUrl.help", "Where LM Studio's local server is reachable. Default works out of the box."),
                    Placeholder = "http://localhost:1234",
                    VisibleWhen = new WizardCondition { StepId = "provider", Operator = "equals", Value = "lmstudio" },
                    Validator = async r => await ValidateLmStudioUrlAsync(r.GetText("lmStudioBaseUrl"), loc),
                },
                new ChoiceStep
                {
                    Id = "lmStudioModel",
                    Title = T("wizard.ai.lmStudioModel.title", "Model"),
                    Help = T("wizard.ai.lmStudioModel.help", "Pick a model loaded in LM Studio. The list is fetched from the server you just confirmed."),
                    VisibleWhen = new WizardCondition { StepId = "provider", Operator = "equals", Value = "lmstudio" },
                    AutoSkipIfChoicesEmpty = true,
                    DynamicChoicesProvider = async r => await FetchLmStudioModelsAsync(r.GetText("lmStudioBaseUrl")),
                },
                new TextStep
                {
                    Id = "lmStudioApiToken",
                    Title = T("wizard.ai.lmStudioToken.title", "API token (optional)"),
                    Help = T("wizard.ai.lmStudioToken.help", "Most local LM Studio installs do not require a token. Leave empty to skip."),
                    VisibleWhen = new WizardCondition { StepId = "provider", Operator = "equals", Value = "lmstudio" },
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

        var provider = result.GetText("provider");
        if (!string.IsNullOrWhiteSpace(provider))
            settings.Provider = provider;

        if (string.Equals(provider, "lmstudio", System.StringComparison.OrdinalIgnoreCase))
        {
            var url = result.GetText("lmStudioBaseUrl");
            if (!string.IsNullOrWhiteSpace(url)) settings.LmStudioBaseUrl = url;
            var model = result.GetText("lmStudioModel");
            if (!string.IsNullOrWhiteSpace(model)) settings.LmStudioModel = model;
            var tok = result.GetText("lmStudioApiToken");
            settings.LmStudioApiToken = tok ?? string.Empty;
        }
        else if (string.Equals(provider, "copilot", System.StringComparison.OrdinalIgnoreCase))
        {
            var path = result.GetText("copilotPath");
            if (!string.IsNullOrWhiteSpace(path)) settings.CopilotPath = path;
            var model = result.GetText("copilotModel");
            settings.CopilotModel = model ?? string.Empty;
        }

        var lang = result.GetText("responseLanguage");
        settings.ResponseLanguage = lang ?? string.Empty;
        return true;
    }

    // ── LM Studio probes ────────────────────────────────────────────

    /// <summary>Returns null on success, error message on failure. Used as the
    /// step's Validator so Next is blocked until LM Studio is reachable.</summary>
    public static async Task<string?> ValidateLmStudioUrlAsync(string? url, Func<string, string>? loc = null)
    {
        string T(string key, string fallback) => loc?.Invoke(key) is { } v && v != key ? v : fallback;

        var baseUrl = string.IsNullOrWhiteSpace(url) ? "http://localhost:1234" : url.TrimEnd('/');
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/models");
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return string.Format(
                    T("wizard.ai.lmStudioUrl.errorStatus", "LM Studio responded {0} {1} at {2}/api/v1/models. Is the local server enabled?"),
                    (int)res.StatusCode, res.ReasonPhrase, baseUrl);
            return null;
        }
        catch (TaskCanceledException)
        {
            return string.Format(
                T("wizard.ai.lmStudioUrl.errorTimeout", "Connection to {0} timed out. Is LM Studio running and the server enabled?"),
                baseUrl);
        }
        catch (Exception ex)
        {
            return string.Format(
                T("wizard.ai.lmStudioUrl.errorReach", "Cannot reach {0}: {1}"),
                baseUrl, ex.Message);
        }
    }

    /// <summary>Lists the LLM models currently exposed by the LM Studio server
    /// at the given URL. Used as the DynamicChoicesProvider for the model step;
    /// returns empty when nothing is loaded so the step auto-skips.</summary>
    public static async Task<IReadOnlyList<WizardChoice>> FetchLmStudioModelsAsync(string? url)
    {
        var baseUrl = string.IsNullOrWhiteSpace(url) ? "http://localhost:1234" : url.TrimEnd('/');
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/models");
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return Array.Empty<WizardChoice>();
            var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var list = new List<WizardChoice>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type != "llm") continue;
                    var key = m.TryGetProperty("key", out var k) ? k.GetString() ?? string.Empty : string.Empty;
                    var display = m.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? key : key;
                    if (string.IsNullOrEmpty(key)) continue;
                    list.Add(new WizardChoice { Value = key, Label = display });
                }
            }
            return list;
        }
        catch
        {
            return Array.Empty<WizardChoice>();
        }
    }
}

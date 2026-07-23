using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>SDK v2 bridge for the story analysis webview.</summary>
public sealed class StoryAnalysisWebViewController : IWebViewController, IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly StoryAnalysisViewModel _vm;

    public event Action<string>? MessagePosted;

    public StoryAnalysisWebViewController(IHostServices host, AiAssistantExtension extension)
    {
        _vm = new StoryAnalysisViewModel(host, extension);
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.FilteredFindings.CollectionChanged += OnFindingsChanged;
    }

    public Task<string?> OnMessageAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "hydrate":
                _vm.RefreshChapters();
                // Present whatever has already been analysed instead of an empty
                // list; the findings arrive via the collection-changed push.
                _ = _vm.LoadStoredFindingsAsync();
                return Task.FromResult<string?>(Snapshot());
            case "selectChapter":
                _vm.SelectedChapter = _vm.AvailableChapters
                    .FirstOrDefault(c => c.Guid == root.GetProperty("guid").GetString());
                _ = _vm.LoadStoredFindingsAsync();
                return Task.FromResult<string?>(null);
            case "analyseChapter":
                _vm.AnalyseCurrentChapterCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "analyseAll":
                _vm.AnalyseAllChaptersCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "analyseStory":
                _vm.AnalyseWholeStoryCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "cancel":
                // Stops the in-flight run and aborts the current model request.
                _vm.CancelAnalysisCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "addToCodex":
                return AddToCodexAsync(
                    root.GetProperty("name").GetString() ?? string.Empty,
                    root.GetProperty("entityType").GetString() ?? string.Empty,
                    root.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty);
            case "setFilter":
                _vm.FilterType = root.GetProperty("filter").GetString() ?? "all";
                return Task.FromResult<string?>(Findings());
            default:
                return Task.FromResult<string?>(null);
        }
    }

    /// <summary>Creates the Codex entry a finding points at, then refreshes the
    /// list so the button disappears for anything now known.</summary>
    private async Task<string?> AddToCodexAsync(string name, string entityType, string description)
    {
        var created = await _vm.AddToCodexAsync(name, entityType, description).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            type = "codexResult",
            created,
            name,
            message = created
                ? _vm.Loc.T("ai.addedToCodex", name)
                : _vm.Loc.T("ai.addToCodexFailed", name),
        }, Json);
    }

    private string Snapshot() =>
        JsonSerializer.Serialize(new
        {
            type = "setup",
            chapters = _vm.AvailableChapters
                .Select(c => new { guid = c.Guid, title = c.Title })
                .ToArray(),
            selectedChapterGuid = _vm.SelectedChapter?.Guid,
            // The filter matches a finding's type exactly, so these values must be
            // the types the analysis actually produces. The view previously offered
            // categories that no finding ever carries, which silently filtered
            // everything away.
            filters = new[]
            {
                new { value = "all", label = _vm.Loc.T("ai.filterAll") },
                new { value = "reference", label = _vm.Loc.T("ai.filterReferences") },
                new { value = "inconsistency", label = _vm.Loc.T("ai.filterInconsistencies") },
                new { value = "suggestion", label = _vm.Loc.T("ai.filterSuggestions") },
            },
            // The view holds no English of its own; every label comes from here
            // so it follows the project language like the rest of the app.
            strings = new Dictionary<string, string>
            {
                ["analyseChapter"] = _vm.Loc.T("ai.analyseChapter"),
                ["analyseAll"] = _vm.Loc.T("ai.analyseAllChapters"),
                ["analyseStory"] = _vm.Loc.T("ai.analyseWholeStory"),
                ["stop"] = _vm.Loc.T("ai.cancelAnalysis"),
                ["stopping"] = _vm.Loc.T("ai.stopping"),
                ["running"] = _vm.Loc.T("ai.running"),
                ["modelThinking"] = _vm.Loc.T("ai.modelThinking"),
                ["modelOutput"] = _vm.Loc.T("ai.modelOutput"),
                ["analysing"] = _vm.Loc.T("ai.analysing"),
                ["noFindings"] = _vm.Loc.T("ai.noFindings"),
                ["notAnalysed"] = _vm.Loc.T("ai.notAnalysed"),
                ["partiallyAnalysed"] = _vm.Loc.T("ai.partiallyAnalysed"),
                ["runToSeeFindings"] = _vm.Loc.T("ai.runToSeeFindings"),
            }
        }, Json);

    private string Findings() =>
        JsonSerializer.Serialize(new
        {
            type = "findings",
            findings = _vm.FilteredFindings
                .Select(f => new
                {
                    findingType = f.Type,
                    title = f.Title,
                    description = f.Description,
                    excerpt = f.Excerpt,
                    chapter = f.ChapterName,
                    entityName = f.EntityName,
                    entityType = f.EntityType,
                    // Only offer to create what the Codex does not already hold.
                    canAddToCodex = _vm.CanAddToCodex(f),
                })
                .ToArray()
        }, Json);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StoryAnalysisViewModel.IsAnalysing)
            or nameof(StoryAnalysisViewModel.ProgressText)
            or nameof(StoryAnalysisViewModel.ProgressCurrent)
            or nameof(StoryAnalysisViewModel.ProgressTotal)
            or nameof(StoryAnalysisViewModel.ProgressActive)
            or nameof(StoryAnalysisViewModel.StreamingThinking)
            or nameof(StoryAnalysisViewModel.StreamingLog)
            or nameof(StoryAnalysisViewModel.AnalysedSceneCount)
            or nameof(StoryAnalysisViewModel.ChapterSceneCount))
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "state",
                isAnalysing = _vm.IsAnalysing,
                progressText = _vm.ProgressText,
                progressCurrent = _vm.ProgressCurrent,
                progressTotal = _vm.ProgressTotal,
                progressActive = _vm.ProgressActive,
                analysedSceneCount = _vm.AnalysedSceneCount,
                chapterSceneCount = _vm.ChapterSceneCount,
                streamingThinking = _vm.StreamingThinking,
                streamingLog = _vm.StreamingLog
            }, Json));
        }
    }

    private void OnFindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MessagePosted?.Invoke(Findings());
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.FilteredFindings.CollectionChanged -= OnFindingsChanged;
        _vm.Dispose();
    }
}

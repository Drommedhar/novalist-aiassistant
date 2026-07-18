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
                return Task.FromResult<string?>(Snapshot());
            case "selectChapter":
                _vm.SelectedChapter = _vm.AvailableChapters
                    .FirstOrDefault(c => c.Guid == root.GetProperty("guid").GetString());
                return Task.FromResult<string?>(null);
            case "analyseChapter":
                _vm.AnalyseCurrentChapterCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "analyseStory":
                _vm.AnalyseWholeStoryCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "setFilter":
                _vm.FilterType = root.GetProperty("filter").GetString() ?? "all";
                return Task.FromResult<string?>(Findings());
            default:
                return Task.FromResult<string?>(null);
        }
    }

    private string Snapshot() =>
        JsonSerializer.Serialize(new
        {
            type = "setup",
            chapters = _vm.AvailableChapters
                .Select(c => new { guid = c.Guid, title = c.Title })
                .ToArray(),
            selectedChapterGuid = _vm.SelectedChapter?.Guid
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
                    chapter = f.ChapterName
                })
                .ToArray()
        }, Json);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StoryAnalysisViewModel.IsAnalysing)
            or nameof(StoryAnalysisViewModel.ProgressText)
            or nameof(StoryAnalysisViewModel.ProgressCurrent)
            or nameof(StoryAnalysisViewModel.ProgressTotal))
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "state",
                isAnalysing = _vm.IsAnalysing,
                progressText = _vm.ProgressText,
                progressCurrent = _vm.ProgressCurrent,
                progressTotal = _vm.ProgressTotal
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

using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.ViewModels;

public partial class StoryAnalysisViewModel : ObservableObject, IDisposable
{
    private readonly IHostServices _host;
    private readonly AiAssistantExtension _extension;
    private readonly IExtensionLocalization _loc;
    private readonly SceneAnalysisService _sceneAnalysis;

    public IExtensionLocalization Loc => _loc;

    [ObservableProperty]
    private bool _isAnalysing;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private int _progressCurrent;

    [ObservableProperty]
    private int _progressTotal;

    /// <summary>How many scenes of the selected chapter have a stored analysis,
    /// and how many there are. Lets the view distinguish "analysed, nothing
    /// found" from "not analysed yet" — which otherwise both look like an empty
    /// list.</summary>
    [ObservableProperty]
    private int _analysedSceneCount;

    [ObservableProperty]
    private int _chapterSceneCount;

    /// <summary>Scenes being analysed right now, comma-joined. With several
    /// prompts in flight a single "current scene" is misleading — the completed
    /// count can sit at zero while four scenes are running.</summary>
    [ObservableProperty]
    private string _progressActive = string.Empty;

    [ObservableProperty]
    private string _streamingLog = string.Empty;

    [ObservableProperty]
    private string _streamingThinking = string.Empty;

    [ObservableProperty]
    private string _filterType = "all";

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private AnalysisChapterOption? _selectedChapter;

    [ObservableProperty]
    private string? _selectedSceneFilter;

    [ObservableProperty]
    private string? _selectedSceneOption;

    [ObservableProperty]
    private bool _hasMultipleScenes;

    public ObservableCollection<AnalysisChapterOption> AvailableChapters { get; } = [];
    public ObservableCollection<string> AvailableScenes { get; } = [];
    public ObservableCollection<string> SceneFilterOptions { get; } = [];
    public ObservableCollection<AnalysisFindingItem> AllFindings { get; } = [];
    public ObservableCollection<AnalysisFindingItem> FilteredFindings { get; } = [];

    private CancellationTokenSource? _cts;
    private readonly object _streamLock = new();
    private readonly StringBuilder _pendingLog = new();
    private readonly StringBuilder _pendingThinking = new();
    private bool _flushScheduled;

    public StoryAnalysisViewModel(IHostServices host, AiAssistantExtension extension)
    {
        _host = host;
        _extension = extension;
        _loc = host.GetLocalization(extension.Id);
        _sceneAnalysis = new SceneAnalysisService(extension.AiService, _loc);
    }

    /// <summary>Adapts a stored finding to the shape the list items expect.</summary>
    private static AiFinding ToFinding(CachedAiFinding f) => new()
    {
        Type = f.Type,
        Title = f.Title,
        Description = f.Description,
        Excerpt = f.Excerpt,
        EntityName = f.EntityName,
        EntityType = f.EntityType,
        ScenePov = f.ScenePov,
        SceneEmotion = f.SceneEmotion,
        SceneIntensity = f.SceneIntensity,
        SceneConflict = f.SceneConflict,
    };

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    partial void OnSelectedSceneOptionChanged(string? value)
    {
        SelectedSceneFilter = value != null && AvailableScenes.Contains(value) ? value : null;
        ApplyFilter();
    }

    /// <summary>Re-filter whenever the type changes, whoever changed it. Setting
    /// the property alone used to leave the list untouched, so a caller that did
    /// not go through the command silently did nothing.</summary>
    partial void OnFilterTypeChanged(string value) => ApplyFilter();

    /// <summary>
    /// Shows the findings already stored for the selected chapter, without
    /// running anything. A scene that has been analysed once keeps its record,
    /// so re-opening the view or switching chapters should present that work
    /// rather than an empty list and a button.
    /// </summary>
    /// <summary>Names already in the Codex, refreshed whenever findings load, so
    /// the view only offers to create what is genuinely missing.</summary>
    private HashSet<string> _knownEntityNames = new(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>True when the finding names an entity the Codex does not hold.</summary>
    public bool CanAddToCodex(AnalysisFindingItem finding)
        => !string.IsNullOrWhiteSpace(finding.EntityName)
           && !_knownEntityNames.Contains(finding.EntityName.Trim());

    private async Task RefreshKnownEntityNamesAsync()
    {
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var c in await _host.EntityService.LoadCharactersAsync().ConfigureAwait(false))
        {
            names.Add(c.DisplayName);
            foreach (var alias in c.Aliases) names.Add(alias);
        }
        foreach (var l in await _host.EntityService.LoadLocationsAsync().ConfigureAwait(false)) names.Add(l.Name);
        foreach (var i in await _host.EntityService.LoadItemsAsync().ConfigureAwait(false)) names.Add(i.Name);
        foreach (var l in await _host.EntityService.LoadLoreAsync().ConfigureAwait(false)) names.Add(l.Name);
        _knownEntityNames = names;
    }

    /// <summary>
    /// Creates the Codex entry a finding points at. Unknown or missing types fall
    /// back to lore, which is the least wrong home for "something the story has"
    /// and is trivially moved afterwards.
    /// </summary>
    public async Task<bool> AddToCodexAsync(string name, string entityType, string description)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var type = (entityType ?? string.Empty).Trim().ToLowerInvariant();
        if (type is not ("character" or "location" or "item" or "lore"))
            type = "lore";

        var id = await _host.EntityService
            .CreateEntityAsync(type, name.Trim(), description ?? string.Empty).ConfigureAwait(false);
        if (string.IsNullOrEmpty(id)) return false;

        _host.EntityService.RequestEntityRefresh();
        await RefreshKnownEntityNamesAsync().ConfigureAwait(false);
        _host.PostToUI(ApplyFilter);   // re-push findings so the button disappears
        return true;
    }

    /// <summary>Recounts how much of the selected chapter has a stored analysis,
    /// without touching the findings list.</summary>
    private async Task RefreshAnalysedCountAsync()
    {
        if (SelectedChapter == null) return;
        var scenes = _host.ProjectService.GetScenesForChapter(SelectedChapter.Guid);
        var analysed = 0;
        foreach (var scene in scenes)
        {
            if (await _host.GetSceneAnalysisAsync(scene.Id).ConfigureAwait(false) != null)
                analysed++;
        }
        _host.PostToUI(() =>
        {
            AnalysedSceneCount = analysed;
            ChapterSceneCount = scenes.Count;
        });
    }

    public async Task LoadStoredFindingsAsync()
    {
        // Before the early returns: findings can be pushed to the view at any
        // time (a live run, a filter change), and each push asks whether the
        // Codex already holds the named entity. An empty set would mark every
        // finding as new and offer to create things that already exist.
        await RefreshKnownEntityNamesAsync().ConfigureAwait(false);

        if (SelectedChapter == null || IsAnalysing) return;

        var chapter = _host.ProjectService.GetChaptersOrdered()
            .FirstOrDefault(c => c.Guid == SelectedChapter.Guid);
        if (chapter == null) return;

        var loaded = new List<AnalysisFindingItem>();
        var sceneNames = new List<string>();
        var scenes = _host.ProjectService.GetScenesForChapter(chapter.Guid);
        var analysed = 0;
        foreach (var scene in scenes)
        {
            var record = await _host.GetSceneAnalysisAsync(scene.Id).ConfigureAwait(false);
            if (record == null) continue;
            analysed++;
            sceneNames.Add(scene.Title);
            loaded.AddRange(record.Findings
                .Select(f => new AnalysisFindingItem(ToFinding(f), chapter.Title, scene.Title)));
        }

        _host.PostToUI(() =>
        {
            AnalysedSceneCount = analysed;
            ChapterSceneCount = scenes.Count;
            AllFindings.Clear();
            foreach (var item in loaded) AllFindings.Add(item);

            AvailableScenes.Clear();
            SceneFilterOptions.Clear();
            SceneFilterOptions.Add(_loc.T("ai.allScenes"));
            foreach (var name in sceneNames.Distinct())
            {
                AvailableScenes.Add(name);
                SceneFilterOptions.Add(name);
            }
            HasMultipleScenes = AvailableScenes.Count > 1;
            SelectedSceneOption = SceneFilterOptions[0];

            ApplyFilter();
            HasResults = AllFindings.Count > 0;
            ProgressText = AllFindings.Count > 0
                ? _loc.T("ai.analysisComplete", AllFindings.Count)
                : string.Empty;
        });
    }

    public void RefreshChapters()
    {
        AvailableChapters.Clear();
        var chapters = _host.ProjectService.GetChaptersOrdered();
        foreach (var ch in chapters)
            AvailableChapters.Add(new AnalysisChapterOption(ch.Guid, ch.Title));
        if (AvailableChapters.Count > 0 && SelectedChapter == null)
            SelectedChapter = AvailableChapters[0];
    }

    [RelayCommand]
    private async Task AnalyseCurrentChapterAsync()
    {
        if (SelectedChapter == null) return;
        var chapter = _host.ProjectService.GetChaptersOrdered()
            .FirstOrDefault(c => c.Guid == SelectedChapter.Guid);
        if (chapter == null) return;
        await AnalyseChaptersAsync([chapter]).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the per-scene analysis over every chapter in the book.
    ///
    /// This is distinct from "analyse whole story", which sends the entire text
    /// as one prompt for a cross-chapter reading and stores nothing. This walks
    /// the same path as a single chapter, so every scene ends up with a stored
    /// record that character knowledge and the focus peek can reuse — and scenes
    /// already analysed cost nothing.
    /// </summary>
    [RelayCommand]
    private async Task AnalyseAllChaptersAsync()
    {
        var chapters = _host.ProjectService.GetChaptersOrdered();
        if (chapters.Count == 0) return;
        await AnalyseChaptersAsync(chapters).ConfigureAwait(false);
    }

    private async Task AnalyseChaptersAsync(IReadOnlyList<ChapterInfo> chaptersToAnalyse)
    {
        IsAnalysing = true;
        AllFindings.Clear();
        FilteredFindings.Clear();
        AvailableScenes.Clear();
        SceneFilterOptions.Clear();
        HasMultipleScenes = false;
        SelectedSceneFilter = null;
        SelectedSceneOption = null;
        HasResults = false;
        StreamingLog = string.Empty;
        StreamingThinking = string.Empty;
        _cts = new CancellationTokenSource();

        try
        {
            var settings = _extension.Settings;
            var chatVm = new AiChatViewModel(_host, _extension);
            var entities = await chatVm.CollectEntitySummariesAsync().ConfigureAwait(false);

            var checks = new EnabledChecks
            {
                References = settings.CheckReferences,
                Inconsistencies = settings.CheckInconsistencies,
                Suggestions = settings.CheckSuggestions,
                SceneStats = settings.CheckSceneStats,
            };

            // Every scene of every chapter in scope, carrying its own chapter so
            // findings and records are attributed correctly when the run spans
            // more than one.
            var sceneTexts = new List<(ChapterInfo Chapter, Sdk.Services.SceneInfo Scene, string Text)>();
            foreach (var ch in chaptersToAnalyse)
            {
                foreach (var s in _host.ProjectService.GetScenesForChapter(ch.Guid))
                {
                    var text = await _host.ProjectService.ReadSceneContentAsync(ch.Guid, s.Id).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                        sceneTexts.Add((ch, s, text));
                }
            }

            _host.PostToUI(() =>
            {
                ProgressTotal = sceneTexts.Count;
                ProgressCurrent = 0;
            });

            var parallelism = _extension.AiService.IsSerialProvider
                ? 1
                : Math.Max(1, _extension.Settings.MaxParallelPrompts);
            var useStreaming = parallelism == 1;
            var gate = new SemaphoreSlim(parallelism, parallelism);

            // Scene titles currently in flight, so the UI can report what is really
            // happening rather than whichever scene happened to start last.
            var active = new List<string>();
            void PublishActive()
            {
                string joined;
                lock (active) joined = string.Join(", ", active);
                _host.PostToUI(() => ProgressActive = joined);
            }

            var sceneTasks = sceneTexts.Select(async pair =>
            {
                var (ch, s, text) = pair;
                await gate.WaitAsync(_cts.Token).ConfigureAwait(false);
                lock (active) active.Add(s.Title);
                PublishActive();
                try
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    if (useStreaming)
                    {
                        _host.PostToUI(() =>
                        {
                            StreamingLog = string.Empty;
                            StreamingThinking = string.Empty;
                            ProgressText = _loc.T("ai.analysingScene", s.Title);
                        });
                        lock (_streamLock)
                        {
                            _pendingLog.Clear();
                            _pendingThinking.Clear();
                        }
                    }
                    else
                    {
                        _host.PostToUI(() =>
                            ProgressText = _loc.T("ai.analysingScene", s.Title));
                    }

                    // One shared door: an unchanged scene is reused verbatim, and a
                    // changed one costs a single call that character knowledge then
                    // gets for free.
                    var request = await SceneAnalysisService.BuildRequestAsync(
                        _host, ch.Guid, ch.Title, s.Id, s.Title, text, entities, checks)
                        .ConfigureAwait(false);
                    var record = await _sceneAnalysis.GetOrCreateAsync(
                        _host, request, _cts.Token,
                        useStreaming ? OnStreamingChunk : null,
                        useStreaming ? OnThinkingChunk : null).ConfigureAwait(false);

                    if (useStreaming)
                        _host.PostToUI(FlushStreamingBuffers);

                    var items = (record?.Findings ?? [])
                        .Select(f => new AnalysisFindingItem(ToFinding(f), ch.Title, s.Title))
                        .ToList();
                    _host.PostToUI(() =>
                    {
                        foreach (var item in items) AllFindings.Add(item);
                        ProgressCurrent++;
                    });
                }
                finally
                {
                    lock (active) active.Remove(s.Title);
                    PublishActive();
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(sceneTasks).ConfigureAwait(false);

            _host.PostToUI(() =>
            {
                var sceneNames = AllFindings.Select(f => f.SceneName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                foreach (var name in sceneNames)
                    AvailableScenes.Add(name);

                SceneFilterOptions.Clear();
                SceneFilterOptions.Add(_loc.T("ai.allScenes"));
                foreach (var name in sceneNames)
                    SceneFilterOptions.Add(name);
                HasMultipleScenes = sceneNames.Count > 1;
                SelectedSceneOption = SceneFilterOptions[0];

                ApplyFilter();
                HasResults = AllFindings.Count > 0;
                ProgressText = _loc.T("ai.analysisComplete", AllFindings.Count);
            });
        }
        catch (OperationCanceledException)
        {
            _host.PostToUI(() => ProgressText = _loc.T("ai.analysisCancelled"));
        }
        catch (Exception ex)
        {
            _host.PostToUI(() => ProgressText = $"Error: {ex.Message}");
        }
        finally
        {
            _host.PostToUI(() =>
            {
                IsAnalysing = false;
                ProgressActive = string.Empty;
            });
            _cts = null;
            // Recount from disk: a cancelled run leaves scenes unanalysed, and
            // the view must say so rather than imply the chapter is complete.
            await RefreshAnalysedCountAsync().ConfigureAwait(false);
            // Refresh what the Codex holds, then re-push so the "Add to Codex"
            // buttons reflect reality for findings produced during the run.
            await RefreshKnownEntityNamesAsync().ConfigureAwait(false);
            _host.PostToUI(ApplyFilter);
        }
    }

    [RelayCommand]
    private async Task AnalyseWholeStoryAsync()
    {
        IsAnalysing = true;
        AllFindings.Clear();
        FilteredFindings.Clear();
        AvailableScenes.Clear();
        SceneFilterOptions.Clear();
        HasMultipleScenes = false;
        SelectedSceneFilter = null;
        SelectedSceneOption = null;
        HasResults = false;
        StreamingLog = string.Empty;
        StreamingThinking = string.Empty;
        _cts = new CancellationTokenSource();

        try
        {
            var chatVm = new AiChatViewModel(_host, _extension);
            var entities = await chatVm.CollectEntitySummariesAsync().ConfigureAwait(false);
            var chapters = _host.ProjectService.GetChaptersOrdered();
            var chapterTexts = new List<ChapterTextEntry>();

            foreach (var ch in chapters)
            {
                var scenes = _host.ProjectService.GetScenesForChapter(ch.Guid);
                var sb = new StringBuilder();
                foreach (var s in scenes)
                {
                    var text = await _host.ProjectService.ReadSceneContentAsync(ch.Guid, s.Id).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(text);
                    }
                }
                if (sb.Length > 0)
                    chapterTexts.Add(new ChapterTextEntry { Name = ch.Title, Text = sb.ToString() });
            }

            _host.PostToUI(() =>
            {
                ProgressText = _loc.T("ai.analysingWholeStory");
                ProgressTotal = 1;
                ProgressCurrent = 0;
            });

            var result = await _extension.AiService.AnalyseWholeStoryAsync(
                chapterTexts, entities, [],
                OnStreamingChunk,
                OnThinkingChunk,
                _cts.Token).ConfigureAwait(false);

            // Flush any remaining buffered streaming output
            _host.PostToUI(FlushStreamingBuffers);

            foreach (var f in result.Findings)
                _host.PostToUI(() => AllFindings.Add(new AnalysisFindingItem(f, "", "")));

            _host.PostToUI(() =>
            {
                ProgressCurrent = 1;
                ApplyFilter();
                HasResults = AllFindings.Count > 0;
                ProgressText = _loc.T("ai.analysisComplete", AllFindings.Count);
            });
        }
        catch (OperationCanceledException)
        {
            _host.PostToUI(() => ProgressText = _loc.T("ai.analysisCancelled"));
        }
        catch (Exception ex)
        {
            _host.PostToUI(() => ProgressText = $"Error: {ex.Message}");
        }
        finally
        {
            _host.PostToUI(() =>
            {
                IsAnalysing = false;
                ProgressActive = string.Empty;
            });
            _cts = null;
            // Recount from disk: a cancelled run leaves scenes unanalysed, and
            // the view must say so rather than imply the chapter is complete.
            await RefreshAnalysedCountAsync().ConfigureAwait(false);
            // Refresh what the Codex holds, then re-push so the "Add to Codex"
            // buttons reflect reality for findings produced during the run.
            await RefreshKnownEntityNamesAsync().ConfigureAwait(false);
            _host.PostToUI(ApplyFilter);
        }
    }

    [RelayCommand]
    private void CancelAnalysis()
    {
        _cts?.Cancel();
        _extension.AiService.Cancel();
    }

    [RelayCommand]
    private void SetFilter(string type)
    {
        FilterType = type;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredFindings.Clear();
        foreach (var f in AllFindings)
        {
            if (FilterType != "all" && f.Type != FilterType)
                continue;
            if (SelectedSceneFilter != null && f.SceneName != SelectedSceneFilter)
                continue;
            FilteredFindings.Add(f);
        }
    }

    private void OnStreamingChunk(string chunk)
    {
        lock (_streamLock)
        {
            _pendingLog.Append(chunk);
            ScheduleFlush();
        }
    }

    private void OnThinkingChunk(string chunk)
    {
        lock (_streamLock)
        {
            _pendingThinking.Append(chunk);
            ScheduleFlush();
        }
    }

    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        _host.PostToUI(FlushStreamingBuffers);
    }

    private void FlushStreamingBuffers()
    {
        string logBatch;
        string thinkingBatch;
        lock (_streamLock)
        {
            logBatch = _pendingLog.ToString();
            thinkingBatch = _pendingThinking.ToString();
            _pendingLog.Clear();
            _pendingThinking.Clear();
            _flushScheduled = false;
        }

        if (logBatch.Length > 0)
            StreamingLog += logBatch;
        if (thinkingBatch.Length > 0)
            StreamingThinking += thinkingBatch;
    }
}

public sealed record AnalysisChapterOption(string Guid, string Title)
{
    public override string ToString() => Title;
}

public sealed class AnalysisFindingItem
{
    public AnalysisFindingItem(AiFinding finding, string chapterName, string sceneName)
    {
        Type = finding.Type;
        Title = finding.Title;
        Description = finding.Description;
        Excerpt = finding.Excerpt;
        EntityName = finding.EntityName;
        EntityType = finding.EntityType;
        ChapterName = chapterName;
        SceneName = sceneName;

        TypeIcon = string.Empty;

        ScenePov = finding.ScenePov;
        SceneEmotion = finding.SceneEmotion;
        SceneIntensity = finding.SceneIntensity;
        SceneConflict = finding.SceneConflict;
    }

    public string Type { get; }
    public string Title { get; }
    public string Description { get; }
    public string Excerpt { get; }
    public string EntityName { get; }
    public string EntityType { get; }
    public string ChapterName { get; }
    public string SceneName { get; }
    public string TypeIcon { get; }
    public bool HasExcerpt => !string.IsNullOrEmpty(Excerpt);
    public bool HasEntity => !string.IsNullOrEmpty(EntityName);
    public bool HasScene => !string.IsNullOrEmpty(SceneName);

    // Scene stats
    public string? ScenePov { get; }
    public string? SceneEmotion { get; }
    public int? SceneIntensity { get; }
    public string? SceneConflict { get; }
    public bool IsSceneStats => Type == "scene_stats";
}

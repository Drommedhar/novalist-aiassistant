using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Extensions.AiAssistant.Views;
using Novalist.Sdk;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant;

public sealed class AiAssistantExtension : IExtension, IRibbonContributor, ISidebarContributor, IContentViewContributor, ISettingsContributor, IGrammarCheckContributor, IContextMenuContributor
{
    public string Id => "com.novalist.ai";
    public string DisplayName => "AI Assistant";
    public string Description => "AI-powered chat, story analysis, and scene statistics.";
    public string Version => "1.0.0";
    public string Author => "Novalist Team";

    private IHostServices _host = null!;
    private IExtensionLocalization _loc = null!;
    internal Services.AiService AiService { get; } = new();
    internal AiSettings Settings { get; private set; } = new();
    private AiGrammarCheckService? _grammarCheckService;
    private CharacterKnowledgeService? _knowledgeService;
    private KnowledgeBuilder? _knowledgeBuilder;
    private InlineRewriteService? _inlineRewriteService;
    private SceneSynopsisService? _synopsisService;

    private AiChatViewModel? _chatVm;
    private CharacterChatViewModel? _characterChatVm;
    private StoryAnalysisViewModel? _analysisVm;
    private AiSettingsViewModel? _settingsVm;
    private bool _isChatVisible;
    private bool _isCharacterChatVisible;
    private bool _isAnalysisVisible;
    private SceneInfo? _lastOpenedScene;
    private List<CharacterInfo> _charactersCache = [];

    internal CharacterKnowledgeService? KnowledgeService => _knowledgeService;
    internal SceneInfo? CurrentScene => _host.ProjectService.CurrentScene ?? _lastOpenedScene;

    // ── IGrammarCheckContributor ────────────────────────────────────

    public string GrammarCheckName => _grammarCheckService?.GrammarCheckName ?? "AI Grammar Check";

    public bool IsGrammarCheckEnabled
    {
        get => _grammarCheckService?.IsGrammarCheckEnabled ?? false;
        set
        {
            if (_grammarCheckService != null)
                _grammarCheckService.IsGrammarCheckEnabled = value;
        }
    }

    public Task<GrammarCheckResult> CheckAsync(string plainText, string language, CancellationToken cancellationToken = default)
    {
        if (_grammarCheckService == null)
            return Task.FromResult(new GrammarCheckResult());
        return _grammarCheckService.CheckAsync(plainText, language, cancellationToken);
    }

    // Icon paths (Lucide)
    private const string IconMessageSquare = "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z";
    private const string IconSearch = "M11 17.25a6.25 6.25 0 1 1 0-12.5 6.25 6.25 0 0 1 0 12.5zm0 0L16.65 22.9";
    private const string IconUser = "M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2 M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z";

    public void Initialize(IHostServices host)
    {
        _host = host;
        _loc = host.GetLocalization(Id);

        LoadSettings();
        ConfigureAiService();
        _grammarCheckService = new AiGrammarCheckService(AiService);
        _grammarCheckService.IsGrammarCheckEnabled = Settings.GrammarCheckEnabled;

        _knowledgeBuilder = new KnowledgeBuilder(AiService);
        _knowledgeService = new CharacterKnowledgeService(host, _knowledgeBuilder, Id);

        _inlineRewriteService = new InlineRewriteService(AiService, host, _loc);
        System.Diagnostics.Debug.WriteLine($"[InlineActions] AiAssistant registering inline contributor. Actions: {string.Join(",", _inlineRewriteService.GetInlineActions().Select(a => a.Id))}");
        host.RegisterInlineActionContributor(_inlineRewriteService);

        _synopsisService = new SceneSynopsisService(AiService, host, _loc);

        host.LanguageChanged += OnLanguageChanged;
        host.SceneOpened += scene =>
        {
            System.Diagnostics.Debug.WriteLine($"[AiAssistant] host.SceneOpened id={scene.Id} title={scene.Title}");
            _lastOpenedScene = scene;
        };
        host.SceneSaved += scene => { _ = OnSceneSavedAsync(scene); };
        host.ProjectLoaded += info => { _ = ReloadCharactersAsync(); };
        _ = ReloadCharactersAsync();
    }

    private async Task ReloadCharactersAsync()
    {
        try
        {
            var chars = await _host.EntityService.LoadCharactersAsync();
            _charactersCache = chars.ToList();
        }
        catch
        {
            _charactersCache = [];
        }
    }

    private async Task OnSceneSavedAsync(SceneInfo scene)
    {
        if (!Settings.EnableCharacterKnowledge || _knowledgeService == null) return;
        try
        {
            // We don't know which characters were in the scene before this
            // save, so invalidate every character whose entry references it.
            // Cheaper than scanning content again, and lazy regen handles it.
            foreach (var character in _charactersCache)
                await _knowledgeService.InvalidateSceneAsync(scene.Id, [character.Id]);
        }
        catch { }
    }

    internal async Task<bool> ClearKnowledgeCacheAsync()
    {
        if (_knowledgeService == null)
        {
            _host.ShowNotification(_loc.T("toast.knowledgeClearFailed"));
            return false;
        }

        try
        {
            await _knowledgeService.ClearCacheAsync();
            Settings.KnowledgeScanCompleted = false;
            SaveSettings();
            _host.ShowNotification(_loc.T("toast.knowledgeClearSuccess"));
            return true;
        }
        catch (Exception ex)
        {
            _host.ShowNotification(string.Format(_loc.T("toast.knowledgeClearFailedReason"), ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Returns the full character roster so the settings page can show a
    /// selection list before the scan starts.
    /// </summary>
    internal async Task<IReadOnlyList<CharacterInfo>> GetAllCharactersAsync()
    {
        var chars = (await _host.EntityService.LoadCharactersAsync()).ToList();
        _charactersCache = chars;
        return chars;
    }

    /// <summary>
    /// Initial scan: caller selects which characters. Each selected character
    /// is run against every scene in story order and the LLM decides presence.
    /// </summary>
    internal async Task RunKnowledgeScanAsync(
        IReadOnlyList<CharacterInfo> selectedCharacters,
        IProgress<KnowledgeScanProgress> progress,
        CancellationToken cancellationToken)
    {
        if (_knowledgeService == null) return;
        if (selectedCharacters.Count == 0) return;

        var ordered = new List<(ChapterInfo Chapter, SceneInfo Scene)>();
        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
                ordered.Add((chapter, scene));
        }

        // LM Studio handles concurrent requests; Copilot CLI runs serial.
        var parallelism = AiService.IsCopilotProvider ? 1 : Math.Max(1, Settings.MaxParallelPrompts);

        await _knowledgeService.ScanAsync(
            selectedCharacters,
            ordered,
            async (chapter, scene) => await _host.ProjectService.ReadSceneContentAsync(chapter.Guid, scene.Id),
            progress,
            cancellationToken,
            parallelism);

        Settings.KnowledgeScanCompleted = true;
        SaveSettings();
    }

    public void Shutdown()
    {
        _host.LanguageChanged -= OnLanguageChanged;
        _chatVm = null;
        _analysisVm = null;
        _settingsVm = null;
    }

    // ── Settings persistence ────────────────────────────────────────

    private void LoadSettings()
    {
        // Read from host settings (AppSettings.Ai) for backwards compatibility
        var json = _host.ReadHostData("ai");
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                Settings = JsonSerializer.Deserialize<AiSettings>(json) ?? new AiSettings();
            }
            catch
            {
                Settings = new AiSettings();
            }
        }
    }

    internal void SaveSettings()
    {
        var json = JsonSerializer.Serialize(Settings);
        _ = _host.WriteHostDataAsync("ai", json);
        ConfigureAiService();
    }

    internal void ConfigureAiService()
    {
        AiService.Configure(Settings);
        var aiLangOverride = Settings.ResponseLanguage;
        AiService.LanguageName = !string.IsNullOrWhiteSpace(aiLangOverride)
            ? aiLangOverride
            : _host.CurrentLanguageDisplayName;

        // Sync grammar check enabled state with settings
        if (_grammarCheckService != null)
            _grammarCheckService.IsGrammarCheckEnabled = Settings.GrammarCheckEnabled;
    }

    private void OnLanguageChanged(string lang)
    {
        if (string.IsNullOrWhiteSpace(Settings.ResponseLanguage))
            AiService.LanguageName = _host.CurrentLanguageDisplayName;
    }

    // ── IContextMenuContributor ─────────────────────────────────────

    public IReadOnlyList<ContextMenuItem> GetContextMenuItems() =>
    [
        new ContextMenuItem
        {
            Context = "Scene",
            Icon = string.Empty,
            Label = _loc.T("contextMenu.generateSynopsis"),
            OnClick = ctx =>
            {
                if (ctx is SceneInfo s && _synopsisService != null)
                {
                    var task = Task.Run(() => _synopsisService.GenerateAndSaveAsync(s.ChapterGuid, s.Id));
                    _ = task.ContinueWith(t =>
                    {
                        if (t.Exception != null)
                            _host.PostToUI(() => _host.ShowNotification($"Synopsis failed: {t.Exception.GetBaseException().Message}"));
                    }, TaskScheduler.Default);
                }
            },
        },
    ];

    // ── IRibbonContributor ──────────────────────────────────────────

    public IReadOnlyList<RibbonItem> GetRibbonItems()
    {
        return
        [
            new RibbonItem
            {
                Tab = "View",
                Group = _loc.T("ribbon.aiGroup"),
                Label = _loc.T("ribbon.aiChat"),
                IconPath = IconMessageSquare,
                Tooltip = _loc.T("ribbon.aiChatTooltip"),
                IsToggle = true,
                IsActive = () => _isChatVisible,
                OnClick = ToggleAiChat,
                Size = "Large",
            },
            new RibbonItem
            {
                Tab = "View",
                Group = _loc.T("ribbon.aiGroup"),
                Label = _loc.T("ribbon.storyAnalysis"),
                IconPath = IconSearch,
                Tooltip = _loc.T("ribbon.storyAnalysisTooltip"),
                IsToggle = true,
                IsActive = () => _isAnalysisVisible,
                OnClick = ToggleStoryAnalysis,
                Size = "Large",
            },
            new RibbonItem
            {
                Tab = "View",
                Group = _loc.T("ribbon.aiGroup"),
                Label = _loc.T("ribbon.characterChat"),
                IconPath = IconUser,
                Tooltip = _loc.T("ribbon.characterChatTooltip"),
                IsToggle = true,
                IsActive = () => _isCharacterChatVisible,
                OnClick = ToggleCharacterChat,
                Size = "Large",
            }
        ];
    }

    private void ToggleCharacterChat()
    {
        _isCharacterChatVisible = !_isCharacterChatVisible;
        _host.ToggleRightSidebar("com.novalist.ai.characterChat");
    }

    private void ToggleAiChat()
    {
        _isChatVisible = !_isChatVisible;
        _host.ToggleRightSidebar("com.novalist.ai.chat");
    }

    private void ToggleStoryAnalysis()
    {
        _isAnalysisVisible = !_isAnalysisVisible;
        if (_isAnalysisVisible)
            _host.ActivateContentView("com.novalist.ai.analysis");
        else
            _host.ActivateContentView("");
    }

    // ── ISidebarContributor (right sidebar for AI Chat) ─────────────

    public IReadOnlyList<SidebarPanel> GetSidebarPanels()
    {
        return
        [
            new SidebarPanel
            {
                Id = "com.novalist.ai.chat",
                Label = _loc.T("ribbon.aiChat"),
                IconPath = IconMessageSquare,
                Side = "Context",
                Tooltip = _loc.T("ribbon.aiChatTooltip"),
                CreateView = () =>
                {
                    _chatVm ??= new AiChatViewModel(_host, this);
                    return new AiChatView { DataContext = _chatVm };
                }
            },
            new SidebarPanel
            {
                Id = "com.novalist.ai.characterChat",
                Label = _loc.T("ribbon.characterChat"),
                IconPath = IconUser,
                Side = "Context",
                Tooltip = _loc.T("ribbon.characterChatTooltip"),
                CreateView = () =>
                {
                    if (_characterChatVm == null)
                    {
                        _characterChatVm = new CharacterChatViewModel(_host, this, () => _knowledgeService);
                        _ = _characterChatVm.ReloadAsync();
                    }
                    return new CharacterChatView { DataContext = _characterChatVm };
                }
            }
        ];
    }

    // ── IContentViewContributor (Story Analysis as full content view) ─

    public IReadOnlyList<ContentViewDescriptor> GetContentViews()
    {
        return
        [
            new ContentViewDescriptor
            {
                ViewKey = "com.novalist.ai.analysis",
                DisplayName = _loc.T("ribbon.storyAnalysis"),
                IconPath = IconSearch,
                CreateView = () =>
                {
                    _analysisVm ??= new StoryAnalysisViewModel(_host, this);
                    return new StoryAnalysisView { DataContext = _analysisVm };
                },
                OnActivated = () =>
                {
                    _isAnalysisVisible = true;
                    _analysisVm?.RefreshChapters();
                },
                OnDeactivated = () => _isAnalysisVisible = false,
            }
        ];
    }

    // ── ISettingsContributor ────────────────────────────────────────

    public IReadOnlyList<SettingsPage> GetSettingsPages()
    {
        return
        [
            new SettingsPage
            {
                Category = _loc.T("settings.ai"),
                IconPath = IconMessageSquare,
                CreateView = () =>
                {
                    _settingsVm ??= new AiSettingsViewModel(this, _loc);
                    return new AiSettingsView { DataContext = _settingsVm };
                },
                OnSave = () => SaveSettings(),
            }
        ];
    }
}

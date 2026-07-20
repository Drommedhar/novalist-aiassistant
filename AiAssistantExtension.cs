using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Extensions.AiAssistant.Views;
using Novalist.Sdk;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant;

public sealed class AiAssistantExtension : IExtension, IRibbonContributor, ISidebarContributor, IContentViewContributor, ISettingsContributor, ISettingsSchemaContributor, IGrammarCheckContributor, IContextMenuContributor, IWizardContributor, Novalist.Sdk.Hooks.IWebViewContributor
{
    public string Id => "com.novalist.ai";
    public string DisplayName => "AI Assistant";
    public string Description => "AI-powered chat, story analysis, and scene statistics.";
    public string Version { get; } = ReadManifestVersion();
    public string Author => "Novalist Team";

    private static string ReadManifestVersion()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(typeof(AiAssistantExtension).Assembly.Location);
            if (asmDir == null) return "0.0.0";
            var manifestPath = Path.Combine(asmDir, "extension.json");
            if (!File.Exists(manifestPath)) return "0.0.0";
            using var stream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "0.0.0" : "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    private IHostServices _host = null!;
    internal IHostServices Host => _host;
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

    private bool _setupWizardChecked;

    public void Initialize(IHostServices host)
    {
        _host = host;
        _loc = host.GetLocalization(Id);

        LoadSettings();
        ConfigureAiService();
        _grammarCheckService = new AiGrammarCheckService(AiService);
        _grammarCheckService.IsGrammarCheckEnabled = Settings.GrammarCheckEnabled;

        _knowledgeBuilder = new KnowledgeBuilder(AiService, host);
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
        host.ProjectLoaded += info =>
        {
            _ = ReloadCharactersAsync();

            // First-run setup wizard: only once per session, and only when
            // nothing is configured yet. Runs after a project is open so the
            // user has context.
            if (!_setupWizardChecked
                && !Settings.Enabled
                && string.IsNullOrWhiteSpace(Settings.LmStudioModel)
                && string.IsNullOrWhiteSpace(Settings.CopilotModel))
            {
                _setupWizardChecked = true;
                _ = RunSetupWizardAsync();
            }
        };
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
        _chatVm?.Dispose();
        _characterChatVm?.Dispose();
        _analysisVm?.Dispose();
        _chatVm = null;
        _characterChatVm = null;
        _analysisVm = null;
        _settingsVm = null;
    }

    // ── IWizardContributor ──────────────────────────────────────────

    public IReadOnlyList<Novalist.Sdk.Models.Wizards.WizardDefinition> GetWizards()
        => new[] { Services.AiSetupWizard.Build(_loc.T) };

    /// <summary>Launches the AI setup wizard via the host, applies the result
    /// to <see cref="Settings"/>, and persists. Safe to call repeatedly — does
    /// nothing if the user cancels.</summary>
    internal async Task RunSetupWizardAsync()
    {
        var definition = Services.AiSetupWizard.Build(_loc.T);

        // Seed current values so the wizard reflects existing settings instead
        // of starting blank when re-launched from the settings page.
        var seed = new Novalist.Sdk.Models.Wizards.WizardResult { DefinitionId = definition.Id };
        seed.Answers["enabled"] = new Novalist.Sdk.Models.Wizards.WizardAnswer
        {
            Text = Settings.Enabled ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(Settings.Provider))
            seed.Answers["provider"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.Provider };
        if (!string.IsNullOrEmpty(Settings.LmStudioBaseUrl))
            seed.Answers["lmStudioBaseUrl"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.LmStudioBaseUrl };
        if (!string.IsNullOrEmpty(Settings.LmStudioModel))
            seed.Answers["lmStudioModel"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.LmStudioModel };
        if (!string.IsNullOrEmpty(Settings.LmStudioApiToken))
            seed.Answers["lmStudioApiToken"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.LmStudioApiToken };
        if (!string.IsNullOrEmpty(Settings.CopilotPath))
            seed.Answers["copilotPath"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.CopilotPath };
        if (!string.IsNullOrEmpty(Settings.CopilotModel))
            seed.Answers["copilotModel"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.CopilotModel };
        if (!string.IsNullOrEmpty(Settings.ResponseLanguage))
            seed.Answers["responseLanguage"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.ResponseLanguage };

        var result = await _host.RunWizardAsync(definition, seed);
        if (result == null || !result.Completed) return;

        Services.AiSetupWizard.Apply(Settings, result);
        SaveSettings();
        _host.ShowNotification(_loc.T("toast.setupComplete"));
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

    // ── ISettingsSchemaContributor (declarative advanced settings) ──
    // The Avalonia AiSettingsView cannot render on the Electron host, so the
    // same AiSettings fields are also exposed as a declarative schema the host
    // renders as a form. Values round-trip through the same "ai" host-data key.

    public SettingsSchema GetSettingsSchema()
    {
        var providerGroup = _loc.T("settings.aiConnection");
        var paramsGroup = _loc.T("settings.aiParameters");
        var checksGroup = _loc.T("settings.aiAnalysisChecks");
        var knowledgeGroup = _loc.T("settings.knowledgeSection");
        return new SettingsSchema
        {
            Title = _loc.T("settings.ai"),
            Fields =
            [
                Bool("enabled", _loc.T("settings.aiEnabled"), Settings.Enabled, providerGroup, _loc.T("settings.aiEnabledDesc")),
                Select("provider", _loc.T("settings.aiProvider"), Settings.Provider, ["lmstudio", "copilot"], providerGroup),
                Text("lmStudioBaseUrl", _loc.T("settings.aiBaseUrl"), Settings.LmStudioBaseUrl, providerGroup),
                Text("lmStudioModel", _loc.T("settings.aiModel"), Settings.LmStudioModel, providerGroup),
                Password("lmStudioApiToken", _loc.T("settings.aiApiToken"), Settings.LmStudioApiToken, providerGroup),
                Text("copilotPath", _loc.T("settings.aiCopilotPath"), Settings.CopilotPath, providerGroup),
                Text("copilotModel", _loc.T("settings.aiCopilotModel"), Settings.CopilotModel, providerGroup),
                Number("temperature", _loc.T("settings.aiTemperature"), Settings.Temperature, 0, 2, paramsGroup),
                Number("contextLength", _loc.T("settings.aiContextLength"), Settings.ContextLength, 0, 131072, paramsGroup),
                Number("topP", "Top P", Settings.TopP, 0, 1, paramsGroup),
                Number("minP", "Min P", Settings.MinP, 0, 1, paramsGroup),
                Number("frequencyPenalty", _loc.T("settings.aiFrequencyPenalty"), Settings.FrequencyPenalty, 0, 2, paramsGroup),
                Number("repeatLastN", _loc.T("settings.aiRepeatLastN"), Settings.RepeatLastN, 0, 1024, paramsGroup),
                Bool("checkReferences", _loc.T("settings.aiCheckReferences"), Settings.CheckReferences, checksGroup, null),
                Bool("checkInconsistencies", _loc.T("settings.aiCheckInconsistencies"), Settings.CheckInconsistencies, checksGroup, null),
                Bool("checkSuggestions", _loc.T("settings.aiCheckSuggestions"), Settings.CheckSuggestions, checksGroup, null),
                Bool("checkSceneStats", _loc.T("settings.aiCheckSceneStats"), Settings.CheckSceneStats, checksGroup, null),
                Bool("disableRegexReferences", _loc.T("settings.aiDisableRegex"), Settings.DisableRegexReferences, checksGroup, null),
                Bool("grammarCheckEnabled", _loc.T("settings.aiGrammarCheckEnabled"), Settings.GrammarCheckEnabled, checksGroup, _loc.T("settings.aiGrammarCheckEnabledDesc")),
                Bool("enableCharacterKnowledge", _loc.T("settings.knowledgeEnable"), Settings.EnableCharacterKnowledge, knowledgeGroup, _loc.T("settings.knowledgeDesc")),
                Number("maxParallelPrompts", _loc.T("settings.knowledgeMaxParallel"), Settings.MaxParallelPrompts, 1, 32, knowledgeGroup),
                Text("responseLanguage", _loc.T("settings.aiResponseLanguage"), Settings.ResponseLanguage, paramsGroup),
                Multiline("systemPrompt", _loc.T("settings.aiSystemPrompt"), Settings.SystemPrompt, paramsGroup, _loc.T("settings.aiSystemPromptDesc")),
            ]
        };
    }

    public Task ApplySettingsAsync(IReadOnlyDictionary<string, string> values)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool ReadBool(string key, bool current)
            => values.TryGetValue(key, out var v) ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : current;
        string ReadStr(string key, string current) => values.TryGetValue(key, out var v) ? v : current;
        double ReadNum(string key, double current, double min, double max)
            => values.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Any, inv, out var n)
                ? Math.Clamp(n, min, max) : current;
        int ReadInt(string key, int current, int min, int max)
            => values.TryGetValue(key, out var v) && int.TryParse(v, System.Globalization.NumberStyles.Any, inv, out var n)
                ? Math.Clamp(n, min, max) : current;

        Settings.Enabled = ReadBool("enabled", Settings.Enabled);
        Settings.Provider = ReadStr("provider", Settings.Provider);
        Settings.LmStudioBaseUrl = ReadStr("lmStudioBaseUrl", Settings.LmStudioBaseUrl);
        Settings.LmStudioModel = ReadStr("lmStudioModel", Settings.LmStudioModel);
        Settings.LmStudioApiToken = ReadStr("lmStudioApiToken", Settings.LmStudioApiToken);
        Settings.CopilotPath = ReadStr("copilotPath", Settings.CopilotPath);
        Settings.CopilotModel = ReadStr("copilotModel", Settings.CopilotModel);
        Settings.Temperature = ReadNum("temperature", Settings.Temperature, 0, 2);
        Settings.ContextLength = ReadInt("contextLength", Settings.ContextLength, 0, 131072);
        Settings.TopP = ReadNum("topP", Settings.TopP, 0, 1);
        Settings.MinP = ReadNum("minP", Settings.MinP, 0, 1);
        Settings.FrequencyPenalty = ReadNum("frequencyPenalty", Settings.FrequencyPenalty, 0, 2);
        Settings.RepeatLastN = ReadInt("repeatLastN", Settings.RepeatLastN, 0, 1024);
        Settings.CheckReferences = ReadBool("checkReferences", Settings.CheckReferences);
        Settings.CheckInconsistencies = ReadBool("checkInconsistencies", Settings.CheckInconsistencies);
        Settings.CheckSuggestions = ReadBool("checkSuggestions", Settings.CheckSuggestions);
        Settings.CheckSceneStats = ReadBool("checkSceneStats", Settings.CheckSceneStats);
        Settings.DisableRegexReferences = ReadBool("disableRegexReferences", Settings.DisableRegexReferences);
        Settings.GrammarCheckEnabled = ReadBool("grammarCheckEnabled", Settings.GrammarCheckEnabled);
        Settings.EnableCharacterKnowledge = ReadBool("enableCharacterKnowledge", Settings.EnableCharacterKnowledge);
        Settings.MaxParallelPrompts = ReadInt("maxParallelPrompts", Settings.MaxParallelPrompts, 1, 32);
        Settings.ResponseLanguage = ReadStr("responseLanguage", Settings.ResponseLanguage);
        Settings.SystemPrompt = ReadStr("systemPrompt", Settings.SystemPrompt);

        SaveSettings();
        return Task.CompletedTask;
    }

    private static SettingsField Text(string key, string label, string value, string? group = null)
        => new() { Key = key, Label = label, Type = SettingsFieldType.Text, Value = value ?? string.Empty, Group = group };
    private static SettingsField Password(string key, string label, string value, string? group)
        => new() { Key = key, Label = label, Type = SettingsFieldType.Password, Value = value ?? string.Empty, Group = group };
    private static SettingsField Multiline(string key, string label, string value, string? group, string? help)
        => new() { Key = key, Label = label, Type = SettingsFieldType.Multiline, Value = value ?? string.Empty, Group = group, Help = help };
    private static SettingsField Bool(string key, string label, bool value, string? group, string? help)
        => new() { Key = key, Label = label, Type = SettingsFieldType.Bool, Value = value ? "true" : "false", Group = group, Help = help };
    private static SettingsField Select(string key, string label, string value, IReadOnlyList<string> options, string? group)
        => new() { Key = key, Label = label, Type = SettingsFieldType.Select, Value = value ?? string.Empty, Options = options, Group = group };
    private static SettingsField Number(string key, string label, double value, double min, double max, string? group)
        => new()
        {
            Key = key, Label = label, Type = SettingsFieldType.Number,
            Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Min = min, Max = max, Group = group
        };

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

    /// <summary>SDK v2: message controllers for the web-hosted panels.</summary>
    public Novalist.Sdk.Hooks.IWebViewController? CreateController(string viewKey) => viewKey switch
    {
        "com.novalist.ai.chat.web" => new Services.ChatWebViewController(_host, this),
        "com.novalist.ai.characterChat.web" =>
            new Services.CharacterChatWebViewController(_host, this, () => _knowledgeService),
        "com.novalist.ai.analysis.web" => new Services.StoryAnalysisWebViewController(_host, this),
        _ => null
    };
}

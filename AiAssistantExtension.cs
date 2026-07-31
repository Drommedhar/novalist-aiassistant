using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Models.Wizards;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant;

public sealed class AiAssistantExtension : IExtension, IStatusBarContributor, IRibbonContributor, ISettingsSchemaContributor, IGrammarCheckContributor, IArticleGeneratorContributor, IEntityExtractionContributor, IContextMenuContributor, IWizardContributor, Novalist.Sdk.Hooks.IWebViewContributor
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
    private CritiqueService? _critiqueService;
    private StyleProfileService? _styleProfiles;
    private DevelopmentalReportService? _report;
    private StoryBibleService? _storyBibleService;
    private OutlineService? _outlineService;
    internal ContextEngine ContextEngine { get; private set; } = new();
    internal IReadOnlyList<SavedPrompt> Prompts { get; private set; } = [];
    private SceneSynopsisService? _synopsisService;
    private ArticleGeneratorService? _articleGenerator;
    private EntityExtractionService? _entityExtractor;

    private bool _isChatVisible;
    private bool _isCharacterChatVisible;
    private bool _isAnalysisVisible;
    private bool _isKnowledgeVisible;
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

    // ── IArticleGeneratorContributor ────────────────────────────────

    public string ArticleGeneratorName => "AI Assistant";

    public bool IsArticleGeneratorEnabled => Settings.Enabled && _articleGenerator != null;

    public Task<ArticleGenerationResult> GenerateAsync(
        ArticleGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (_articleGenerator == null)
            return Task.FromResult(new ArticleGenerationResult { Error = _loc.T("article.noModel") });
        return _articleGenerator.GenerateAsync(request, cancellationToken);
    }

    // ── IEntityExtractionContributor ────────────────

    public string EntityExtractorName => "AI Assistant";

    public bool IsEntityExtractorEnabled => Settings.Enabled && _entityExtractor != null;

    public Task<EntityExtractionResult> ExtractAsync(
        EntityExtractionRequest request, CancellationToken cancellationToken = default)
    {
        if (_entityExtractor == null)
            return Task.FromResult(new EntityExtractionResult { Error = _loc.T("extract.noModel") });
        return _entityExtractor.ExtractAsync(request, cancellationToken);
    }

    // Icon paths (Lucide)
    private const string IconMessageSquare = "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z";
    private const string IconSearch = "M11 17.25a6.25 6.25 0 1 1 0-12.5 6.25 6.25 0 0 1 0 12.5zm0 0L16.65 22.9";
    private const string IconUser = "M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2 M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z";
    private const string IconBook = "M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z";

    private bool _setupWizardChecked;
    private bool _legacyKnowledgeChecked;

    // Background analysis state, surfaced in the status bar so a pass the user
    // did not start is never invisible.
    private int _backgroundAnalysisToken;
    private volatile string _backgroundSceneTitle = string.Empty;
    private volatile bool _backgroundRunning;
    private int _backgroundAnalysed;

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

        // The prompt list is read through a callback rather than passed by
        // value, so editing a prompt shows up in the menu without a restart.
        _inlineRewriteService = new InlineRewriteService(AiService, host, _loc, () => Prompts);
        System.Diagnostics.Debug.WriteLine($"[InlineActions] AiAssistant registering inline contributor. Actions: {string.Join(",", _inlineRewriteService.GetInlineActions().Select(a => a.Id))}");
        host.RegisterInlineActionContributor(_inlineRewriteService);

        _critiqueService = new CritiqueService(AiService, host, _loc);
        _report = new DevelopmentalReportService(AiService, host, _loc);
        _styleProfiles = new StyleProfileService(
            AiService, host, _loc, Path.Combine(host.GetExtensionSettingsPath(Id), "styles.json"));
        // Everything that writes prose goes through the inline service's system
        // prompts, so the voice is applied in exactly one place.
        _inlineRewriteService.StyleProfiles = _styleProfiles;
        _storyBibleService = new StoryBibleService(AiService, host, _loc);
        _outlineService = new OutlineService(AiService, host, _loc);
        LoadPromptsAndContext();
        RegisterAiCommands();

        _synopsisService = new SceneSynopsisService(AiService, host, _loc);
        _articleGenerator = new ArticleGeneratorService(AiService, _loc);
        _entityExtractor = new EntityExtractionService(AiService, _loc);

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

            _ = AskAboutLegacyKnowledgeAsync();
        };
        _ = ReloadCharactersAsync();
    }

    /// <summary>
    /// On first open after the upgrade, asks what to do with character knowledge
    /// built by the old per-character pass. Asked once per project — the answer
    /// is remembered — and only when such data actually exists.
    /// </summary>
    private async Task AskAboutLegacyKnowledgeAsync()
    {
        try
        {
            if (_legacyKnowledgeChecked) return;
            _legacyKnowledgeChecked = true;

            if (!string.IsNullOrEmpty(Settings.KnowledgeMigrationChoice)) return;
            if (_knowledgeService == null) return;

            var stored = await _knowledgeService.CountStoredCharactersAsync();
            if (stored == 0)
            {
                // Nothing to decide about; record that so we never ask later,
                // once the new pipeline has written entries of its own.
                Settings.KnowledgeMigrationChoice = Services.KnowledgeMigrationWizard.KeepValue;
                SaveSettings();
                return;
            }

            var result = await _host.RunWizardAsync(
                Services.KnowledgeMigrationWizard.Build(_loc.T, stored));
            if (result == null || !result.Completed) return;   // ask again next time

            var choice = result.GetText(Services.KnowledgeMigrationWizard.StepId);
            if (string.Equals(choice, Services.KnowledgeMigrationWizard.ClearValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                await _knowledgeService.ClearCacheAsync();
                _host.ShowNotification(_loc.T("toast.knowledgeCleared"));
            }

            Settings.KnowledgeMigrationChoice =
                string.IsNullOrWhiteSpace(choice)
                    ? Services.KnowledgeMigrationWizard.KeepValue
                    : choice;
            SaveSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiAssistant] legacy knowledge prompt failed: {ex.GetType().Name}");
        }
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

            if (Settings.BackgroundSceneAnalysis)
                await AnalyseSceneInBackgroundAsync(scene);
        }
        catch { }
    }

    /// <summary>
    /// Re-analyses a saved scene so the record is ready before anything asks for
    /// it. Opt-in, because it spends model time without the user initiating it,
    /// and deliberately quiet: a failure here must never interrupt writing.
    /// </summary>
    private async Task AnalyseSceneInBackgroundAsync(SceneInfo scene)
    {
        if (!Settings.Enabled) return;

        // Coalesce rapid saves: only the last edit of a burst is worth analysing.
        var token = Interlocked.Increment(ref _backgroundAnalysisToken);
        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (Volatile.Read(ref _backgroundAnalysisToken) != token) return;

        try
        {
            var chapter = _host.ProjectService.GetChaptersOrdered()
                .FirstOrDefault(c => c.Guid == scene.ChapterGuid);
            if (chapter == null) return;

            var sceneText = await _host.ProjectService
                .ReadSceneContentAsync(chapter.Guid, scene.Id).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sceneText)) return;
            if (!await _host.IsSceneAnalysisStaleAsync(scene.Id, sceneText).ConfigureAwait(false))
                return;

            var chatVm = new ViewModels.AiChatViewModel(_host, this);
            var entities = await chatVm.CollectEntitySummariesAsync().ConfigureAwait(false);
            var checks = new EnabledChecks
            {
                References = Settings.CheckReferences,
                Inconsistencies = Settings.CheckInconsistencies,
                Suggestions = Settings.CheckSuggestions,
                SceneStats = Settings.CheckSceneStats,
            };

            var request = await Services.SceneAnalysisService.BuildRequestAsync(
                _host, chapter.Guid, chapter.Title, scene.Id, scene.Title,
                sceneText, entities, checks).ConfigureAwait(false);

            // The renderer polls status items once a second, so setting the
            // fields is enough — there is no refresh call to make.
            _backgroundSceneTitle = scene.Title;
            _backgroundRunning = true;
            try
            {
                var record = await new Services.SceneAnalysisService(AiService, _loc)
                    .GetOrCreateAsync(_host, request, CancellationToken.None).ConfigureAwait(false);
                if (record != null) Interlocked.Increment(ref _backgroundAnalysed);
            }
            finally
            {
                _backgroundRunning = false;
                _backgroundSceneTitle = string.Empty;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AiAssistant] background scene analysis failed: {ex.GetType().Name}");
        }
    }

    // ── IStatusBarContributor ───────────────────────────────────────

    public IReadOnlyList<StatusBarItem> GetStatusBarItems() =>
    [
        new StatusBarItem
        {
            Id = "ai.backgroundAnalysis",
            Alignment = "Right",
            Order = 60,
            GetText = () =>
            {
                if (!Settings.BackgroundSceneAnalysis) return string.Empty;
                if (_backgroundRunning)
                    return _loc.T("status.analysingScene", _backgroundSceneTitle);
                var done = Volatile.Read(ref _backgroundAnalysed);
                return done > 0 ? _loc.T("status.scenesAnalysed", done) : string.Empty;
            },
            GetTooltip = () => _loc.T("status.backgroundTooltip"),
        },
    ];

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

        // One pass per scene, not one per character-and-scene. The shared scene
        // record already describes every present character, so the scan costs a
        // call per scene instead of characters x scenes — and a scene another
        // feature already analysed costs nothing at all.
        var chatVm = new ViewModels.AiChatViewModel(_host, this);
        var entities = await chatVm.CollectEntitySummariesAsync().ConfigureAwait(false);
        var checks = new EnabledChecks
        {
            References = Settings.CheckReferences,
            Inconsistencies = Settings.CheckInconsistencies,
            Suggestions = Settings.CheckSuggestions,
            SceneStats = Settings.CheckSceneStats,
        };
        var sceneAnalysis = new Services.SceneAnalysisService(AiService, _loc);

        for (var i = 0; i < ordered.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (chapter, scene) = ordered[i];

            var sceneText = await _host.ProjectService
                .ReadSceneContentAsync(chapter.Guid, scene.Id).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sceneText)) continue;

            progress.Report(new KnowledgeScanProgress
            {
                OverallDone = i,
                OverallTotal = ordered.Count,
                SceneIndex = i + 1,
                SceneTotal = ordered.Count,
                SceneTitle = scene.Title,
                ChapterTitle = chapter.Title,
                CharacterTotal = selectedCharacters.Count,
            });

            var request = await Services.SceneAnalysisService.BuildRequestAsync(
                _host, chapter.Guid, chapter.Title, scene.Id, scene.Title,
                sceneText, entities, checks).ConfigureAwait(false);
            var record = await sceneAnalysis
                .GetOrCreateAsync(_host, request, cancellationToken).ConfigureAwait(false);
            if (record == null) continue;   // model unreachable; leave the scene for a later pass

            await _knowledgeService
                .ApplyRecordAsync(record, selectedCharacters, sceneText, cancellationToken)
                .ConfigureAwait(false);
        }

        progress.Report(new KnowledgeScanProgress
        {
            OverallDone = ordered.Count,
            OverallTotal = ordered.Count,
            SceneTotal = ordered.Count,
            CharacterTotal = selectedCharacters.Count,
        });

        Settings.KnowledgeScanCompleted = true;
        SaveSettings();
    }

    public void Shutdown()
    {
        _host.LanguageChanged -= OnLanguageChanged;
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
        if (!string.IsNullOrEmpty(Settings.ClaudePath))
            seed.Answers["claudePath"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.ClaudePath };
        if (!string.IsNullOrEmpty(Settings.ClaudeModel))
            seed.Answers["claudeModel"] = new Novalist.Sdk.Models.Wizards.WizardAnswer { Text = Settings.ClaudeModel };
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
    // The AiSettings fields are exposed as a declarative schema the host
    // renders as a form. Values round-trip through the same "ai" host-data key.

    /// <summary>The names as the picker shows them.</summary>
    private IReadOnlyList<string> StyleNames
        => _styleProfiles?.Profiles.Select(p => p.Name).ToList() ?? [];

    private string ActiveStyleName
        => _styleProfiles?.Active?.Name ?? _loc.T("settings.styleNone");

    /// <summary>
    /// Reads the writer's own prose and derives a description of how they write.
    /// Long enough to want reporting - it is one model call over a sample
    /// gathered from across the book, not a settings toggle.
    /// </summary>
    /// <summary>
    /// One report on the whole book, filed on the research shelf. Long enough to
    /// want reporting: it is a model call per section over the whole thing.
    /// </summary>
    private async Task BuildReportAsync()
    {
        if (_report == null) return;

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("report.command"),
            IsIndeterminate = true,
            AllowCancel = true,
        });
        var built = await _report.BuildAsync(
            new Progress<string>(where => progress.SetStatus(where)),
            progress.CancellationToken);
        progress.Dispose();

        _host.ShowNotification(built.Error
            ?? _loc.T("report.done").Replace("{0}", built.Sections.ToString()));
    }

    private async Task BuildStyleProfileAsync()
    {
        if (_styleProfiles == null) return;

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("settings.styleBuild"),
            IsIndeterminate = true,
            AllowCancel = true,
        });
        // Named for the book it was read from, so a writer with two voices can
        // tell which is which.
        var books = _host.ProjectService.GetBooks();
        var name = books.FirstOrDefault(b => b.Id == _host.ProjectService.ActiveBookId)?.Name
            ?? string.Empty;

        var built = await _styleProfiles.BuildAsync(
            name,
            new Progress<string>(where => progress.SetStatus(where)),
            progress.CancellationToken);
        progress.Dispose();

        if (built != null)
        {
            _host.ShowNotification(_loc.T("style.built")
                .Replace("{0}", built.SampledWords.ToString()));
        }
    }

    public SettingsSchema GetSettingsSchema()
    {
        var providerGroup = _loc.T("settings.aiConnection");
        var paramsGroup = _loc.T("settings.aiParameters");
        var checksGroup = _loc.T("settings.aiAnalysisChecks");
        var knowledgeGroup = _loc.T("settings.knowledgeSection");
        var styleGroup = _loc.T("settings.styleSection");
        return new SettingsSchema
        {
            Title = _loc.T("settings.ai"),
            Fields =
            [
                Bool("enabled", _loc.T("settings.aiEnabled"), Settings.Enabled, providerGroup, _loc.T("settings.aiEnabledDesc")),
                Select("provider", _loc.T("settings.aiProvider"), Settings.Provider, ["lmstudio", "anthropic", "copilot", "claude"], providerGroup),
                Text("lmStudioBaseUrl", _loc.T("settings.aiBaseUrl"), Settings.LmStudioBaseUrl, providerGroup, "provider", LmStudio),
                Text("lmStudioModel", _loc.T("settings.aiModel"), Settings.LmStudioModel, providerGroup, "provider", LmStudio, _availableModels),
                Password("lmStudioApiToken", _loc.T("settings.aiApiToken"), Settings.LmStudioApiToken, providerGroup, "provider", LmStudio),
                Select("openAiCompatiblePreset", _loc.T("settings.aiPreset"), Settings.OpenAiCompatiblePreset,
                    [.. AiSettings.OpenAiCompatiblePresets.Keys], providerGroup),
                Password("anthropicApiKey", _loc.T("settings.aiAnthropicKey"), Settings.AnthropicApiKey, providerGroup, "provider", Anthropic),
                Text("anthropicModel", _loc.T("settings.aiAnthropicModel"), Settings.AnthropicModel, providerGroup, "provider", Anthropic, _availableModels),
                Text("anthropicBaseUrl", _loc.T("settings.aiAnthropicBaseUrl"), Settings.AnthropicBaseUrl, providerGroup, "provider", Anthropic),
                Text("copilotPath", _loc.T("settings.aiCopilotPath"), Settings.CopilotPath, providerGroup, "provider", Copilot),
                Text("copilotModel", _loc.T("settings.aiCopilotModel"), Settings.CopilotModel, providerGroup, "provider", Copilot, _availableModels),
                Text("claudePath", _loc.T("settings.aiClaudePath"), Settings.ClaudePath, providerGroup, "provider", Claude),
                Text("claudeModel", _loc.T("settings.aiClaudeModel"), Settings.ClaudeModel, providerGroup, "provider", Claude, _availableModels),
                Action("refreshModels", _loc.T("settings.aiRefreshModels"), providerGroup, null, []),
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
                Bool("backgroundSceneAnalysis", _loc.T("settings.backgroundAnalysis"), Settings.BackgroundSceneAnalysis, knowledgeGroup, _loc.T("settings.backgroundAnalysisDesc")),
                Select("styleProfile", _loc.T("settings.styleProfile"), ActiveStyleName,
                    [_loc.T("settings.styleNone"), .. StyleNames], styleGroup),
                Action("buildStyleProfile", _loc.T("settings.styleBuild"), styleGroup, null, []),
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
        Settings.ClaudePath = ReadStr("claudePath", Settings.ClaudePath);
        Settings.ClaudeModel = ReadStr("claudeModel", Settings.ClaudeModel);
        Settings.AnthropicApiKey = ReadStr("anthropicApiKey", Settings.AnthropicApiKey);
        Settings.AnthropicModel = ReadStr("anthropicModel", Settings.AnthropicModel);
        Settings.AnthropicBaseUrl = ReadStr("anthropicBaseUrl", Settings.AnthropicBaseUrl);
        // Picking a preset fills the base URL in; typing a URL by hand leaves it.
        Settings.OpenAiCompatiblePreset = ReadStr("openAiCompatiblePreset", Settings.OpenAiCompatiblePreset);
        if (AiSettings.BaseUrlForPreset(Settings.OpenAiCompatiblePreset) is { } presetUrl)
            Settings.LmStudioBaseUrl = presetUrl;
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
        Settings.BackgroundSceneAnalysis = ReadBool("backgroundSceneAnalysis", Settings.BackgroundSceneAnalysis);
        Settings.ResponseLanguage = ReadStr("responseLanguage", Settings.ResponseLanguage);
        Settings.SystemPrompt = ReadStr("systemPrompt", Settings.SystemPrompt);

        // The picker carries a name, because that is what it shows. An unknown
        // name - "none", or a profile deleted since the form was drawn - clears
        // the choice rather than leaving a voice the writer thinks is off.
        if (values.TryGetValue("styleProfile", out var styleName) && _styleProfiles != null)
        {
            _styleProfiles.SetActive(_styleProfiles.Profiles
                .FirstOrDefault(p => p.Name == styleName)?.Id);
        }

        SaveSettings();
        return Task.CompletedTask;
    }

    public async Task<SettingsSchema?> ExecuteSchemaActionAsync(string actionKey, IReadOnlyDictionary<string, string> values)
    {
        if (actionKey == "buildStyleProfile")
        {
            await BuildStyleProfileAsync();
            return GetSettingsSchema();
        }
        if (actionKey != "refreshModels") return null;

        // Reflect the form's (possibly unsaved) connection settings so the model
        // list matches what the user is about to save, then query the provider.
        string Read(string key, string current) => values.TryGetValue(key, out var v) ? v : current;
        Settings.Provider = Read("provider", Settings.Provider);
        Settings.LmStudioBaseUrl = Read("lmStudioBaseUrl", Settings.LmStudioBaseUrl);
        Settings.LmStudioApiToken = Read("lmStudioApiToken", Settings.LmStudioApiToken);
        Settings.CopilotPath = Read("copilotPath", Settings.CopilotPath);
        Settings.ClaudePath = Read("claudePath", Settings.ClaudePath);
        ConfigureAiService();

        try
        {
            var models = await AiService.ListModelsAsync();
            _availableModels = models
                .Select(m => m.Key)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            _availableModels = [];
        }
        return GetSettingsSchema();
    }

    private static readonly string[] LmStudio = ["lmstudio"];
    private static readonly string[] Anthropic = ["anthropic"];
    private static readonly string[] Copilot = ["copilot"];
    private static readonly string[] Claude = ["claude"];

    // Models fetched from the active provider by the "Refresh models" action,
    // offered as autocomplete suggestions on the model fields.
    private List<string> _availableModels = [];

    private static SettingsField Text(string key, string label, string value, string? group = null, string? whenKey = null, IReadOnlyList<string>? whenValues = null, IReadOnlyList<string>? suggestions = null)
        => new() { Key = key, Label = label, Type = SettingsFieldType.Text, Value = value ?? string.Empty, Group = group, VisibleWhenKey = whenKey, VisibleWhenValues = whenValues, Suggestions = suggestions };
    private static SettingsField Action(string key, string label, string? group, string? whenKey, IReadOnlyList<string> whenValues)
        => new() { Key = key, Label = label, Type = SettingsFieldType.Action, Group = group, VisibleWhenKey = whenKey, VisibleWhenValues = whenValues };
    private static SettingsField Password(string key, string label, string value, string? group, string? whenKey = null, IReadOnlyList<string>? whenValues = null)
        => new() { Key = key, Label = label, Type = SettingsFieldType.Password, Value = value ?? string.Empty, Group = group, VisibleWhenKey = whenKey, VisibleWhenValues = whenValues };
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
            },
            new RibbonItem
            {
                Tab = "View",
                Group = _loc.T("ribbon.aiGroup"),
                Label = _loc.T("ribbon.knowledge"),
                IconPath = IconBook,
                Tooltip = _loc.T("ribbon.knowledgeTooltip"),
                IsToggle = true,
                IsActive = () => _isKnowledgeVisible,
                OnClick = ToggleKnowledge,
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

    private void ToggleKnowledge()
    {
        _isKnowledgeVisible = !_isKnowledgeVisible;
        _host.ActivateContentView(_isKnowledgeVisible ? "com.novalist.ai.knowledge" : "");
    }

    private void ToggleStoryAnalysis()
    {
        _isAnalysisVisible = !_isAnalysisVisible;
        if (_isAnalysisVisible)
            _host.ActivateContentView("com.novalist.ai.analysis");
        else
            _host.ActivateContentView("");
    }

    /// <summary>SDK v2: message controllers for the web-hosted panels.</summary>
    public Novalist.Sdk.Hooks.IWebViewController? CreateController(string viewKey) => viewKey switch
    {
        "com.novalist.ai.chat.web" => new Services.ChatWebViewController(_host, this),
        "com.novalist.ai.characterChat.web" =>
            new Services.CharacterChatWebViewController(_host, this, () => _knowledgeService),
        "com.novalist.ai.analysis.web" => new Services.StoryAnalysisWebViewController(_host, this),
        "com.novalist.ai.knowledge.web" => new Services.KnowledgeWebViewController(_host, this),
        _ => null
    };

    // ── The features that write into the project ────────────────────
    //
    // Every one of these is a command rather than a button, for two reasons.
    // They are all long-running things a writer starts deliberately, and a
    // command is addressable - which means the scripting surface can drive them,
    // and the writer can bind a key to the one they use.

    private static readonly string[] AiCommandIds =
    [
        "com.novalist.ai.critique.scene",
        "com.novalist.ai.critique.book",
        "com.novalist.ai.bible",
        "com.novalist.ai.outline",
        "com.novalist.ai.style",
        "com.novalist.ai.report",
    ];

    /// <summary>
    /// One command per reader, so the palette shows who is available and a key
    /// can be bound to the read the writer actually uses. The developmental read
    /// keeps the original id above rather than gaining a second one.
    /// </summary>
    private static string LensCommandId(string lensKey)
        => $"com.novalist.ai.critique.scene.{lensKey}";

    private void RegisterAiCommands()
    {
        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = AiCommandIds[0],
                Title = _loc.T("critique.sceneCommand"),
                Description = _loc.T("critique.sceneDescription"),
                Mutates = true,
                ArgumentsSchema =
                    """
                    {"type":"object","properties":{
                      "proposeEdits":{"type":"boolean",
                        "description":"Also propose wordings, as suggested edits."},
                      "lens":{"type":"string","enum":[LENS_KEYS],
                        "description":"Who is reading. Omitted is the developmental read."}}}
                    """.Replace("LENS_KEYS", LensKeysJson),
            },
            argumentsJson => CritiqueOpenSceneAsync(
                ReadFlag(argumentsJson, "proposeEdits"), ReadText(argumentsJson, "lens")));

        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = AiCommandIds[1],
                Title = _loc.T("critique.bookCommand"),
                Description = _loc.T("critique.bookDescription"),
                Mutates = true,
                ArgumentsSchema =
                    """
                    {"type":"object","properties":{"proposeEdits":{"type":"boolean"},
                      "lens":{"type":"string","enum":[LENS_KEYS]}}}
                    """.Replace("LENS_KEYS", LensKeysJson),
            },
            argumentsJson => CritiqueBookAsync(
                ReadFlag(argumentsJson, "proposeEdits"), ReadText(argumentsJson, "lens")));

        // The other readers. Skipping the first is not a special case - it is
        // the command registered above, under the id it has always had.
        foreach (var lens in CritiqueLenses.All.Skip(1))
        {
            var key = lens.Key;
            _host.RegisterCommand(
                new HostCommandInfo
                {
                    Id = LensCommandId(key),
                    Title = _loc.T("critique.sceneAs").Replace("{0}", _loc.T($"critique.lens.{key}")),
                    Description = _loc.T($"critique.lens.{key}Description"),
                    Mutates = true,
                    ArgumentsSchema =
                        """
                        {"type":"object","properties":{"proposeEdits":{"type":"boolean"}}}
                        """,
                },
                argumentsJson => CritiqueOpenSceneAsync(
                    ReadFlag(argumentsJson, "proposeEdits"), key));
        }

        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = AiCommandIds[5],
                Title = _loc.T("report.command"),
                Description = _loc.T("report.description"),
                Mutates = true,
            },
            _ => BuildReportAsync());

        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = AiCommandIds[4],
                Title = _loc.T("settings.styleBuild"),
                Description = _loc.T("style.commandDescription"),
                Mutates = false,
            },
            _ => BuildStyleProfileAsync());

        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = AiCommandIds[2],
                Title = _loc.T("bible.command"),
                Description = _loc.T("bible.description"),
                Mutates = true,
            },
            _ => BootstrapBibleAsync());

        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = AiCommandIds[3],
                Title = _loc.T("outline.command"),
                Description = _loc.T("outline.description"),
                Mutates = true,
                ArgumentsSchema =
                    """
                    {"type":"object","required":["premise"],"properties":{
                      "premise":{"type":"string"},
                      "chapters":{"type":"integer"},
                      "structure":{"type":"string"}}}
                    """,
            },
            OutlineFromPremiseAsync);
    }

    private void UnregisterAiCommands()
    {
        foreach (var id in AiCommandIds) _host.UnregisterCommand(id);
        foreach (var lens in CritiqueLenses.All.Skip(1))
            _host.UnregisterCommand(LensCommandId(lens.Key));
    }

    /// <summary>The lens keys as a JSON array body, for the argument schemas.</summary>
    private static string LensKeysJson { get; } =
        string.Join(",", CritiqueLenses.All.Select(l => $"\"{l.Key}\""));

    /// <summary>
    /// Critiques the scene the writer is looking at. The open scene rather than a
    /// named one, because that is the scene they are asking about.
    /// </summary>
    private async Task CritiqueOpenSceneAsync(bool proposeEdits, string? lensKey = null)
    {
        var scene = _host.ProjectService.CurrentScene ?? _lastOpenedScene;
        if (scene == null || string.IsNullOrEmpty(scene.ChapterGuid))
        {
            _host.ShowNotification(_loc.T("critique.noScene"));
            return;
        }

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("critique.sceneAs")
                .Replace("{0}", _loc.T($"critique.lens.{CritiqueLenses.Resolve(lensKey).Key}")),
            InitialStatus = scene.Title,
            IsIndeterminate = true,
            AllowCancel = true,
        });

        var report = await _critiqueService!.CritiqueSceneAsync(
            scene.ChapterGuid, scene.Id, proposeEdits, lensKey, progress.CancellationToken);
        progress.Dispose();
        Report(report);
    }

    private async Task CritiqueBookAsync(bool proposeEdits, string? lensKey = null)
    {
        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("critique.bookCommand"),
            IsIndeterminate = true,
            AllowCancel = true,
        });

        var report = await _critiqueService!.CritiqueBookAsync(
            proposeEdits,
            new Progress<string>(where => progress.SetStatus(where)),
            lensKey,
            progress.CancellationToken);
        progress.Dispose();
        Report(report);
    }

    private void Report(CritiqueReport report)
    {
        if (report.Error != null)
        {
            _host.ShowNotification(report.Error);
            return;
        }
        _host.ShowNotification(report.Suggestions > 0
            ? _loc.T("critique.doneWithEdits")
                .Replace("{0}", report.Comments.ToString())
                .Replace("{1}", report.Suggestions.ToString())
            : _loc.T("critique.done").Replace("{0}", report.Comments.ToString()));
    }

    /// <summary>
    /// Reads the book and offers what it found. Nothing is created until the
    /// writer has seen the list - a bad pass over a whole novel would otherwise
    /// leave a hundred entries to delete one at a time.
    /// </summary>
    private async Task BootstrapBibleAsync()
    {
        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("bible.command"),
            InitialStatus = _loc.T("bible.reading"),
            IsIndeterminate = true,
            AllowCancel = true,
        });

        var report = await _storyBibleService!.ProposeAsync(
            new Progress<string>(where => progress.SetStatus(where)),
            progress.CancellationToken);
        progress.Dispose();

        if (report.Error != null)
        {
            _host.ShowNotification(report.Error);
            return;
        }
        if (report.Proposals.Count == 0)
        {
            _host.ShowNotification(_loc.T("bible.nothingNew"));
            return;
        }

        var approved = await ApproveProposalsAsync(report);
        if (approved.Count == 0) return;

        var created = await _storyBibleService.CreateAsync(approved);
        _host.EntityService.RequestEntityRefresh();
        _host.ShowNotification(_loc.T("bible.created").Replace("{0}", created.ToString()));
    }

    /// <summary>
    /// Shows what was found and lets the writer pick. A multi-select is the whole
    /// safeguard here, so it is not skippable and nothing is pre-approved beyond
    /// what the pass is confident about.
    /// </summary>
    private async Task<List<BibleProposal>> ApproveProposalsAsync(BibleReport report)
    {
        var choices = report.Proposals.Select(p => new WizardChoice
        {
            Value = p.Name,
            Label = $"{p.Name} ({Kind(p.TypeKey)})",
            Description = string.IsNullOrWhiteSpace(p.Summary)
                ? _loc.T("bible.foundIn").Replace("{0}", p.FoundIn.Count.ToString())
                : p.Summary,
        }).ToList();

        var result = await _host.RunWizardAsync(new WizardDefinition
        {
            Id = "com.novalist.ai.bible.approve",
            DisplayName = _loc.T("bible.command"),
            Description = _loc.T("bible.approveHelp").Replace("{0}", report.ScenesRead.ToString()),
            Scope = WizardScope.Project,
            Steps =
            [
                new ChoiceStep
                {
                    Id = "approved",
                    Title = _loc.T("bible.approveTitle"),
                    Help = _loc.T("bible.approveHint"),
                    Skippable = false,
                    MultiSelect = true,
                    Choices = choices,
                },
            ],
        });

        if (result is not { Completed: true }) return [];

        var picked = new HashSet<string>(result.GetMulti("approved"), StringComparer.Ordinal);
        return [.. report.Proposals.Where(p => picked.Contains(p.Name))];
    }

    private string Kind(string typeKey) => _loc.T($"bible.kind.{typeKey}");

    /// <summary>
    /// Premise in, binder out. Asked for through a wizard when no arguments were
    /// given, because a premise is a paragraph and a command palette is not where
    /// somebody writes one.
    /// </summary>
    private async Task OutlineFromPremiseAsync(string? argumentsJson)
    {
        string premise;
        var chapters = 24;
        var structure = string.Empty;

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            var answers = await _host.RunWizardAsync(new WizardDefinition
            {
                Id = "com.novalist.ai.outline.ask",
                DisplayName = _loc.T("outline.command"),
                Description = _loc.T("outline.wizardHelp"),
                Scope = WizardScope.Project,
                Steps =
                [
                    new TextStep
                    {
                        Id = "premise",
                        Title = _loc.T("outline.premiseTitle"),
                        Help = _loc.T("outline.premiseHelp"),
                        Multiline = true,
                        Skippable = false,
                    },
                    new NumberStep
                    {
                        Id = "chapters",
                        Title = _loc.T("outline.chaptersTitle"),
                        Min = 3,
                        Max = 80,
                        DefaultValue = 24,
                    },
                    new TextStep
                    {
                        Id = "structure",
                        Title = _loc.T("outline.structureTitle"),
                        Help = _loc.T("outline.structureHelp"),
                    },
                ],
            });

            if (answers is not { Completed: true }) return;
            premise = answers.GetText("premise");
            chapters = answers.GetNumber("chapters", 24);
            structure = answers.GetText("structure");
        }
        else
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
                premise = document.RootElement.TryGetProperty("premise", out var p)
                    ? p.GetString() ?? string.Empty
                    : string.Empty;
                if (document.RootElement.TryGetProperty("chapters", out var c)
                    && c.TryGetInt32(out var count)) chapters = count;
                if (document.RootElement.TryGetProperty("structure", out var st))
                    structure = st.GetString() ?? string.Empty;
            }
            catch (System.Text.Json.JsonException)
            {
                _host.ShowNotification(_loc.T("outline.badRequest"));
                return;
            }
        }

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("outline.command"),
            InitialStatus = _loc.T("outline.thinking"),
            IsIndeterminate = true,
            AllowCancel = true,
        });

        var outline = await _outlineService!.ProposeAsync(
            premise, chapters, structure, progress.CancellationToken);
        if (outline.Error != null)
        {
            progress.Dispose();
            _host.ShowNotification(outline.Error);
            return;
        }
        if (outline.Chapters.Count == 0)
        {
            progress.Dispose();
            return;
        }

        progress.SetStatus(_loc.T("outline.building"));
        var built = await _outlineService.MaterialiseAsync(outline, progress.CancellationToken);
        progress.Dispose();

        _host.ShowNotification(_loc.T("outline.built")
            .Replace("{0}", built.Chapters.ToString())
            .Replace("{1}", built.Scenes.ToString())
            .Replace("{2}", built.Plotlines.ToString()));
    }

    // ── The writer's own prompts, and the context budget ────────────

    private string PromptsPath => Path.Combine(
        _host.GetExtensionSettingsPath(Id), "prompts.json");

    private string ContextPath => Path.Combine(
        _host.GetExtensionSettingsPath(Id), "context.json");

    /// <summary>
    /// Loads the prompt library and the context budget.
    ///
    /// A first run seeds the library with a few examples rather than an empty box.
    /// Nobody writes a good prompt from a blank field; they read one and change it.
    /// </summary>
    private void LoadPromptsAndContext()
    {
        try
        {
            if (File.Exists(PromptsPath))
            {
                Prompts = PromptLibrary.Deserialise(File.ReadAllText(PromptsPath));
            }
            else
            {
                Prompts = PromptLibrary.Examples();
                File.WriteAllText(PromptsPath, PromptLibrary.Serialise(Prompts));
            }

            ContextEngine = ContextEngine.Load(
                File.Exists(ContextPath) ? File.ReadAllText(ContextPath) : null);
        }
        catch (IOException)
        {
            // Custom prompts are worth having and not worth failing to start over.
            Prompts = PromptLibrary.Examples();
        }
    }

    internal void SavePrompts(IReadOnlyList<SavedPrompt> prompts)
    {
        Prompts = PromptLibrary.Clean(prompts);
        try
        {
            File.WriteAllText(PromptsPath, PromptLibrary.Serialise(Prompts));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A string argument, or null when it was not given.</summary>
    private static string? ReadText(string? argumentsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool ReadFlag(string? argumentsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

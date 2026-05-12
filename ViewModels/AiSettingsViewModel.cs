using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.ViewModels;

public partial class AiSettingsViewModel : ObservableObject
{
    private readonly AiAssistantExtension _extension;
    private readonly IExtensionLocalization _loc;
    private AiSettings Settings => _extension.Settings;
    private bool _isLoading;

    [ObservableProperty] private bool _aiEnabled;
    [ObservableProperty] private string _aiProvider = "lmstudio";
    [ObservableProperty] private string _aiBaseUrl = string.Empty;
    [ObservableProperty] private string _aiModel = string.Empty;
    [ObservableProperty] private string _aiApiToken = string.Empty;
    [ObservableProperty] private string _aiCopilotPath = "copilot";
    [ObservableProperty] private string _aiCopilotModel = string.Empty;
    [ObservableProperty] private double _aiTemperature;
    [ObservableProperty] private int _aiContextLength;
    [ObservableProperty] private double _aiTopP;
    [ObservableProperty] private double _aiMinP;
    [ObservableProperty] private double _aiFrequencyPenalty;
    [ObservableProperty] private int _aiRepeatLastN;
    [ObservableProperty] private bool _aiCheckReferences;
    [ObservableProperty] private bool _aiCheckInconsistencies;
    [ObservableProperty] private bool _aiCheckSuggestions;
    [ObservableProperty] private bool _aiCheckSceneStats;
    [ObservableProperty] private bool _aiDisableRegexReferences;
    [ObservableProperty] private bool _aiGrammarCheckEnabled = true;
    [ObservableProperty] private bool _aiEnableCharacterKnowledge;
    [ObservableProperty] private bool _aiKnowledgeScanCompleted;
    [ObservableProperty] private int _aiMaxParallelPrompts = 4;

    // Scan flow:
    //   "Idle" — show "Configure scan" button
    //   "Selecting" — show character checklist + Start
    //   "Running" — show progress overlay
    //   "Done" — show summary
    [ObservableProperty] private string _knowledgeScanState = "Idle";

    [ObservableProperty] private ObservableCollection<CharacterSelectItem> _knowledgeCharacterChoices = [];

    [ObservableProperty] private double _aiKnowledgeScanProgress;
    [ObservableProperty] private string _aiKnowledgeScanOverallLine = string.Empty;
    [ObservableProperty] private string _aiKnowledgeScanCountsLine = string.Empty;
    [ObservableProperty] private string _aiKnowledgeScanEtaLine = string.Empty;
    [ObservableProperty] private ObservableCollection<KnowledgeActiveLine> _aiKnowledgeScanActive = [];
    [ObservableProperty] private string _aiKnowledgeScanFinalSummary = string.Empty;

    public bool KnowledgeScanIdle => KnowledgeScanState == "Idle";
    public bool KnowledgeScanSelecting => KnowledgeScanState == "Selecting";
    public bool KnowledgeScanRunning => KnowledgeScanState == "Running";
    public bool KnowledgeScanDone => KnowledgeScanState == "Done";

    partial void OnKnowledgeScanStateChanged(string value)
    {
        OnPropertyChanged(nameof(KnowledgeScanIdle));
        OnPropertyChanged(nameof(KnowledgeScanSelecting));
        OnPropertyChanged(nameof(KnowledgeScanRunning));
        OnPropertyChanged(nameof(KnowledgeScanDone));
    }

    private CancellationTokenSource? _scanCts;
    [ObservableProperty] private string _aiResponseLanguage = string.Empty;
    [ObservableProperty] private string _aiSystemPrompt = string.Empty;
    [ObservableProperty] private ObservableCollection<AiModelListItem> _availableAiModels = [];
    [ObservableProperty] private bool _isLoadingModels;
    [ObservableProperty] private string _aiServerStatus = string.Empty;

    public bool IsLmStudioProvider => AiProvider == "lmstudio";
    public bool IsCopilotProvider => AiProvider == "copilot";

    public List<AiProviderItem> AvailableAiProviders { get; } =
    [
        new("lmstudio", "LM Studio"),
        new("copilot", "GitHub Copilot CLI"),
    ];

    public IExtensionLocalization Loc => _loc;

    public AiSettingsViewModel(AiAssistantExtension extension, IExtensionLocalization loc)
    {
        _extension = extension;
        _loc = loc;
        LoadSettings();
    }

    private void LoadSettings()
    {
        _isLoading = true;
        var ai = Settings;
        AiEnabled = ai.Enabled;
        AiProvider = ai.Provider;
        AiBaseUrl = ai.LmStudioBaseUrl;
        AiModel = ai.LmStudioModel;
        AiApiToken = ai.LmStudioApiToken;
        AiCopilotPath = ai.CopilotPath;
        AiCopilotModel = ai.CopilotModel;
        AiTemperature = ai.Temperature;
        AiContextLength = ai.ContextLength;
        AiTopP = ai.TopP;
        AiMinP = ai.MinP;
        AiFrequencyPenalty = ai.FrequencyPenalty;
        AiRepeatLastN = ai.RepeatLastN;
        AiCheckReferences = ai.CheckReferences;
        AiCheckInconsistencies = ai.CheckInconsistencies;
        AiCheckSuggestions = ai.CheckSuggestions;
        AiCheckSceneStats = ai.CheckSceneStats;
        AiDisableRegexReferences = ai.DisableRegexReferences;
        AiGrammarCheckEnabled = ai.GrammarCheckEnabled;
        AiEnableCharacterKnowledge = ai.EnableCharacterKnowledge;
        AiKnowledgeScanCompleted = ai.KnowledgeScanCompleted;
        AiMaxParallelPrompts = ai.MaxParallelPrompts;
        AiResponseLanguage = ai.ResponseLanguage;
        AiSystemPrompt = ai.SystemPrompt;
        _isLoading = false;
    }

    private void SaveAndNotify()
    {
        if (_isLoading) return;
        _extension.SaveSettings();
    }

    partial void OnAiEnabledChanged(bool value) { Settings.Enabled = value; SaveAndNotify(); }
    partial void OnAiProviderChanged(string value) { Settings.Provider = value; OnPropertyChanged(nameof(IsLmStudioProvider)); OnPropertyChanged(nameof(IsCopilotProvider)); SaveAndNotify(); }
    partial void OnAiBaseUrlChanged(string value) { Settings.LmStudioBaseUrl = value; SaveAndNotify(); }
    partial void OnAiModelChanged(string value) { Settings.LmStudioModel = value; SaveAndNotify(); }
    partial void OnAiApiTokenChanged(string value) { Settings.LmStudioApiToken = value; SaveAndNotify(); }
    partial void OnAiCopilotPathChanged(string value) { Settings.CopilotPath = value; SaveAndNotify(); }
    partial void OnAiCopilotModelChanged(string value) { Settings.CopilotModel = value; SaveAndNotify(); }
    partial void OnAiTemperatureChanged(double value) { Settings.Temperature = Math.Clamp(value, 0, 2); SaveAndNotify(); }
    partial void OnAiContextLengthChanged(int value) { Settings.ContextLength = Math.Max(0, value); SaveAndNotify(); }
    partial void OnAiTopPChanged(double value) { Settings.TopP = Math.Clamp(value, 0, 1); SaveAndNotify(); }
    partial void OnAiMinPChanged(double value) { Settings.MinP = Math.Clamp(value, 0, 1); SaveAndNotify(); }
    partial void OnAiFrequencyPenaltyChanged(double value) { Settings.FrequencyPenalty = Math.Clamp(value, 0, 2); SaveAndNotify(); }
    partial void OnAiRepeatLastNChanged(int value) { Settings.RepeatLastN = Math.Max(0, value); SaveAndNotify(); }
    partial void OnAiCheckReferencesChanged(bool value) { Settings.CheckReferences = value; SaveAndNotify(); }
    partial void OnAiCheckInconsistenciesChanged(bool value) { Settings.CheckInconsistencies = value; SaveAndNotify(); }
    partial void OnAiCheckSuggestionsChanged(bool value) { Settings.CheckSuggestions = value; SaveAndNotify(); }
    partial void OnAiCheckSceneStatsChanged(bool value) { Settings.CheckSceneStats = value; SaveAndNotify(); }
    partial void OnAiDisableRegexReferencesChanged(bool value) { Settings.DisableRegexReferences = value; SaveAndNotify(); }
    partial void OnAiGrammarCheckEnabledChanged(bool value) { Settings.GrammarCheckEnabled = value; SaveAndNotify(); }
    partial void OnAiEnableCharacterKnowledgeChanged(bool value) { Settings.EnableCharacterKnowledge = value; SaveAndNotify(); }
    partial void OnAiKnowledgeScanCompletedChanged(bool value) { Settings.KnowledgeScanCompleted = value; SaveAndNotify(); }
    partial void OnAiMaxParallelPromptsChanged(int value) { Settings.MaxParallelPrompts = Math.Clamp(value, 1, 32); SaveAndNotify(); }
    partial void OnAiResponseLanguageChanged(string value) { Settings.ResponseLanguage = value; SaveAndNotify(); }
    partial void OnAiSystemPromptChanged(string value) { Settings.SystemPrompt = value; SaveAndNotify(); }

    [RelayCommand]
    private async Task BeginKnowledgeScanAsync()
    {
        if (KnowledgeScanState != "Idle" && KnowledgeScanState != "Done") return;

        var characters = await _extension.GetAllCharactersAsync();
        var items = new ObservableCollection<CharacterSelectItem>();
        foreach (var c in characters.OrderBy(c => c.DisplayName, System.StringComparer.CurrentCultureIgnoreCase))
            items.Add(new CharacterSelectItem(c) { IsSelected = true });
        KnowledgeCharacterChoices = items;
        KnowledgeScanState = "Selecting";
    }

    [RelayCommand]
    private void CancelKnowledgeScanSelection()
    {
        KnowledgeScanState = "Idle";
    }

    [RelayCommand]
    private void ToggleAllKnowledgeCharacters()
    {
        var anyUnselected = KnowledgeCharacterChoices.Any(c => !c.IsSelected);
        foreach (var c in KnowledgeCharacterChoices) c.IsSelected = anyUnselected;
    }

    [RelayCommand]
    private async Task StartKnowledgeScanAsync()
    {
        var selected = KnowledgeCharacterChoices.Where(c => c.IsSelected).Select(c => c.Source).ToList();
        if (selected.Count == 0) return;

        AiKnowledgeScanFinalSummary = string.Empty;

        using var busy = _extension.Host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("settings.knowledgeScanRunningTitle"),
            InitialStatus = _loc.T("settings.knowledgeScanStarting"),
            IsIndeterminate = false,
            ShowProgressBar = true,
            AllowCancel = true,
            CancelLabel = _loc.T("settings.knowledgeCancel"),
        });

        _scanCts?.Cancel();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(busy.CancellationToken);

        var activeLineFmt = _loc.T("settings.knowledgeActiveLine");
        var overallFmt = _loc.T("settings.knowledgeProgressOverall");
        var countsFmt = _loc.T("settings.knowledgeProgressCounts");
        var etaFmt = _loc.T("settings.knowledgeProgressEta");

        var progress = new System.Progress<KnowledgeScanProgress>(p =>
        {
            busy.SetProgress(p.OverallFraction);
            busy.SetStatus(string.Format(overallFmt, p.OverallDone, p.OverallTotal));

            var detail = new List<string>();
            foreach (var s in p.ActiveSlots)
                detail.Add(string.Format(activeLineFmt, s.CharacterName, s.ChapterTitle, s.SceneTitle));
            detail.Add(string.Format(countsFmt, p.StoredPresent, p.StoredAbsent, p.Reused));
            detail.Add(string.Format(etaFmt,
                p.EstimatedRemainingMs > 0 ? FormatDuration(p.EstimatedRemainingMs) : "—",
                FormatDuration(p.ElapsedMs),
                p.AverageStepMs > 0 ? FormatDuration(p.AverageStepMs) : "—"));
            busy.SetDetails(detail);
        });

        KnowledgeScanState = "Idle"; // dialog handles UI; reset inline state
        try
        {
            await _extension.RunKnowledgeScanAsync(selected, progress, _scanCts.Token);
            AiKnowledgeScanCompleted = true;
            _extension.Host.ShowNotification(_loc.T("settings.knowledgeScanComplete"));
        }
        catch (System.OperationCanceledException)
        {
            _extension.Host.ShowNotification(_loc.T("settings.knowledgeScanCancelled"));
        }
        catch (System.Exception ex)
        {
            _extension.Host.ShowNotification(ex.Message);
        }
    }

    [RelayCommand]
    private void CancelKnowledgeScanRunning()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private void CloseKnowledgeScanResult()
    {
        KnowledgeScanState = "Idle";
    }

    [RelayCommand]
    private async Task ClearKnowledgeCacheAsync()
    {
        await _extension.ClearKnowledgeCacheAsync();
        AiKnowledgeScanCompleted = false;
        AiKnowledgeScanFinalSummary = _loc.T("settings.knowledgeCacheCleared");
        AiKnowledgeScanOverallLine = string.Empty;
        AiKnowledgeScanCountsLine = string.Empty;
        AiKnowledgeScanEtaLine = string.Empty;
        AiKnowledgeScanActive.Clear();
        AiKnowledgeScanProgress = 0;
    }

    private static string FormatDuration(long ms)
    {
        if (ms <= 0) return "—";
        var ts = System.TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds:D2}s";
        if (ts.TotalSeconds >= 1) return $"{(int)ts.TotalSeconds}s";
        return $"{ms}ms";
    }

    [RelayCommand]
    private async Task TestAiConnectionAsync()
    {
        AiServerStatus = _loc.T("settings.aiTesting");
        try
        {
            _extension.ConfigureAiService();
            var running = await _extension.AiService.IsServerRunningAsync();
            AiServerStatus = running ? _loc.T("settings.aiConnected") : _loc.T("settings.aiNotReachable");
        }
        catch
        {
            AiServerStatus = _loc.T("settings.aiNotReachable");
        }
    }

    [RelayCommand]
    private async Task RefreshAiModelsAsync()
    {
        IsLoadingModels = true;
        try
        {
            _extension.ConfigureAiService();
            var models = await _extension.AiService.ListModelsAsync();
            AvailableAiModels = new ObservableCollection<AiModelListItem>(
                models.Select(m => new AiModelListItem(m.Key, m.DisplayName, m.SizeBytes)));
        }
        catch
        {
            AvailableAiModels = [];
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    [RelayCommand]
    private void ResetAiSystemPrompt()
    {
        AiSystemPrompt = string.Empty;
    }

    [RelayCommand]
    private void SelectAiModel(string? modelKey)
    {
        if (string.IsNullOrEmpty(modelKey)) return;
        if (IsCopilotProvider)
            AiCopilotModel = modelKey;
        else
            AiModel = modelKey;
    }

    [RelayCommand]
    private void SetAiProvider(string? provider)
    {
        if (!string.IsNullOrEmpty(provider))
        {
            AiProvider = provider;
            AvailableAiModels = [];
            AiServerStatus = string.Empty;
        }
    }
}

public sealed class KnowledgeActiveLine
{
    public string Display { get; init; } = string.Empty;
}

public partial class CharacterSelectItem : ObservableObject
{
    public CharacterInfo Source { get; }
    public string DisplayName => Source.DisplayName;
    public string Role => Source.Role;

    [ObservableProperty]
    private bool _isSelected = true;

    public CharacterSelectItem(CharacterInfo source) { Source = source; }
}

public sealed class AiModelListItem(string key, string displayName, long sizeBytes)
{
    public string Key { get; } = key;
    public string DisplayName { get; } = displayName;
    public long SizeBytes { get; } = sizeBytes;
    public string SizeDisplay => SizeBytes > 0 ? $"{SizeBytes / (1024.0 * 1024 * 1024):F1} GB" : "";

    public override string ToString() => string.IsNullOrEmpty(SizeDisplay) ? DisplayName : $"{DisplayName} ({SizeDisplay})";
}

public sealed class AiProviderItem(string key, string displayName)
{
    public string Key { get; } = key;
    public string DisplayName { get; } = displayName;

    public override string ToString() => DisplayName;
}

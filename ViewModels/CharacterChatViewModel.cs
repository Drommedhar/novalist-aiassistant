using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.ViewModels;

public partial class CharacterChatViewModel : ObservableObject
{
    private readonly IHostServices _host;
    private readonly AiAssistantExtension _extension;
    private readonly IExtensionLocalization _loc;
    private readonly Func<CharacterKnowledgeService?> _knowledgeAccessor;

    public IExtensionLocalization Loc => _loc;

    [ObservableProperty]
    private ObservableCollection<CharacterInfo> _characters = [];

    [ObservableProperty]
    private CharacterInfo? _selectedCharacter;

    [ObservableProperty]
    private ObservableCollection<SceneOption> _scenes = [];

    [ObservableProperty]
    private SceneOption? _selectedScene;

    [ObservableProperty]
    private bool _includeSceneKnowledge = true;

    [ObservableProperty]
    private bool _includeCharacterImage;

    [ObservableProperty]
    private string _userInput = string.Empty;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _isPreparingKnowledge;

    [ObservableProperty]
    private double _preparationProgress;

    [ObservableProperty]
    private string _preparationStatus = string.Empty;

    [ObservableProperty]
    private string _streamingResponse = string.Empty;

    public ObservableCollection<CharacterChatTurn> Turns { get; } = [];

    private readonly List<AiChatMessage> _history = [];
    private CancellationTokenSource? _cts;
    private bool _systemPromptDirty = true;

    public CharacterChatViewModel(IHostServices host, AiAssistantExtension extension, Func<CharacterKnowledgeService?> knowledgeAccessor)
    {
        _host = host;
        _extension = extension;
        _loc = host.GetLocalization(extension.Id);
        _knowledgeAccessor = knowledgeAccessor;

        _host.ProjectLoaded += info => { _ = ReloadAsync(); };
        _host.SceneOpened += scene => { _ = OnSceneOpenedAsync(scene); };
    }

    public async Task ReloadAsync()
    {
        Debug.WriteLine($"[CharacterChat] ReloadAsync start. _extension.CurrentScene={_extension.CurrentScene?.Id ?? "null"} title={_extension.CurrentScene?.Title ?? ""}");
        Turns.Clear();
        _history.Clear();
        _systemPromptDirty = true;

        var chars = (await _host.EntityService.LoadCharactersAsync())
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Characters = new ObservableCollection<CharacterInfo>(chars);
        SelectedCharacter = chars.FirstOrDefault();

        var sceneList = new List<SceneOption> { new() { Id = string.Empty, DisplayLabel = _loc.T("characterChat.noSceneContext") } };
        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                sceneList.Add(new SceneOption
                {
                    Id = scene.Id,
                    ChapterGuid = chapter.Guid,
                    ChapterTitle = chapter.Title,
                    SceneTitle = scene.Title,
                    DisplayLabel = $"{chapter.Title} → {scene.Title}"
                });
            }
        }
        Scenes = new ObservableCollection<SceneOption>(sceneList);

        // Default to the currently open scene if any; otherwise first real scene.
        var currentSceneId = _extension.CurrentScene?.Id;
        Debug.WriteLine($"[CharacterChat] Scenes.Count={Scenes.Count} currentSceneId={currentSceneId ?? "null"}");
        SceneOption? preselect = null;
        if (!string.IsNullOrEmpty(currentSceneId))
        {
            preselect = Scenes.FirstOrDefault(s => string.Equals(s.Id, currentSceneId, StringComparison.OrdinalIgnoreCase));
            Debug.WriteLine($"[CharacterChat] preselect match={(preselect != null ? preselect.DisplayLabel : "NO MATCH")}");
        }
        SelectedScene = preselect
            ?? Scenes.FirstOrDefault(s => !string.IsNullOrEmpty(s.Id))
            ?? Scenes.First();
        Debug.WriteLine($"[CharacterChat] SelectedScene set to id={SelectedScene?.Id} label={SelectedScene?.DisplayLabel}");
    }

    private Task OnSceneOpenedAsync(SceneInfo scene)
    {
        Debug.WriteLine($"[CharacterChat] SceneOpened event id={scene.Id} title={scene.Title}. Scenes.Count={Scenes.Count}");
        var match = Scenes.FirstOrDefault(s => string.Equals(s.Id, scene.Id, StringComparison.OrdinalIgnoreCase));
        Debug.WriteLine($"[CharacterChat] SceneOpened match={(match != null ? match.DisplayLabel : "NO MATCH")}");
        if (match != null)
            SelectedScene = match;
        return Task.CompletedTask;
    }

    partial void OnSelectedCharacterChanged(CharacterInfo? value) => _systemPromptDirty = true;
    partial void OnSelectedSceneChanged(SceneOption? value) => _systemPromptDirty = true;
    partial void OnIncludeSceneKnowledgeChanged(bool value) => _systemPromptDirty = true;
    partial void OnIncludeCharacterImageChanged(bool value) => _systemPromptDirty = true;

    [RelayCommand]
    private void ResetChat()
    {
        Turns.Clear();
        _history.Clear();
        _systemPromptDirty = true;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (SelectedCharacter == null) return;
        var text = UserInput.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (IsGenerating || IsPreparingKnowledge) return;

        UserInput = string.Empty;
        Turns.Add(new CharacterChatTurn(_loc.T("characterChat.userLabel"), text, false));

        try
        {
            string? attachedImagePath = null;
            bool firstTurnAfterSystem = false;
            if (_systemPromptDirty)
            {
                _history.Clear();
                var systemPrompt = await BuildSystemPromptAsync();
                var systemMsg = new AiChatMessage { Role = "system", Content = systemPrompt };
                _history.Add(systemMsg);
                _systemPromptDirty = false;
                firstTurnAfterSystem = true;

                if (IncludeCharacterImage && SelectedCharacter != null)
                {
                    attachedImagePath = await _host.EntityService.GetCharacterImagePathAsync(
                        SelectedCharacter.Id,
                        SelectedScene?.ChapterGuid,
                        SelectedScene?.Id);
                    Debug.WriteLine($"[CharacterChat] Image resolve for {SelectedCharacter.DisplayName} → {(attachedImagePath ?? "NULL")}");
                }
            }

            // Attach image to the FIRST user turn (vision models accept images on
            // user messages; some reject them on system messages).
            var userMsg = new AiChatMessage { Role = "user", Content = text };
            if (firstTurnAfterSystem && !string.IsNullOrEmpty(attachedImagePath))
            {
                userMsg.ImagePaths = [attachedImagePath];
                userMsg.Content = $"[Reference image of {SelectedCharacter?.DisplayName} attached.]\n\n{text}";
                Debug.WriteLine($"[CharacterChat] Attaching image to first user message: {attachedImagePath}");
            }
            _history.Add(userMsg);

            IsGenerating = true;
            StreamingResponse = string.Empty;
            _cts = new CancellationTokenSource();

            var result = await _extension.AiService.GenerateChatAsync(
                _history,
                chunk =>
                {
                    StreamingResponse += chunk;
                },
                cancellationToken: _cts.Token);

            _history.Add(new AiChatMessage { Role = "assistant", Content = result.Response });
            Turns.Add(new CharacterChatTurn(SelectedCharacter.DisplayName, result.Response, true));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Turns.Add(new CharacterChatTurn(_loc.T("characterChat.errorLabel"), ex.Message, false));
        }
        finally
        {
            IsGenerating = false;
            StreamingResponse = string.Empty;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private async Task<string> BuildSystemPromptAsync()
    {
        if (SelectedCharacter == null) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"You ARE the character \"{SelectedCharacter.DisplayName}\". Speak in first person, in-character.");
        sb.AppendLine($"Always respond in {_extension.AiService.LanguageName}.");
        sb.AppendLine();
        sb.AppendLine("CHARACTER PROFILE:");
        sb.Append("Name: ").AppendLine(SelectedCharacter.DisplayName);
        if (!string.IsNullOrWhiteSpace(SelectedCharacter.Role))
            sb.Append("Role: ").AppendLine(SelectedCharacter.Role);
        if (SelectedCharacter.Aliases.Count > 0)
            sb.Append("Aliases: ").AppendLine(string.Join(", ", SelectedCharacter.Aliases));
        sb.AppendLine();

        if (IncludeSceneKnowledge && SelectedScene != null && !string.IsNullOrEmpty(SelectedScene.Id))
        {
            var knowledge = await BuildCumulativeKnowledgeAsync();
            if (!string.IsNullOrWhiteSpace(knowledge))
            {
                sb.AppendLine($"WHAT YOU KNOW UP TO AND INCLUDING THE SCENE \"{SelectedScene.SceneTitle}\":");
                sb.AppendLine("(You only know what is listed below. Do NOT reveal information from later scenes.)");
                sb.AppendLine();
                sb.AppendLine(knowledge);
            }
            else
            {
                sb.AppendLine($"You have not yet experienced anything notable up to scene \"{SelectedScene.SceneTitle}\".");
            }
        }

        return sb.ToString();
    }

    private async Task<string> BuildCumulativeKnowledgeAsync()
    {
        var ks = _knowledgeAccessor();
        if (ks == null || SelectedCharacter == null || SelectedScene == null) return string.Empty;
        if (string.IsNullOrEmpty(SelectedScene.Id)) return string.Empty;

        var ordered = new List<(ChapterInfo Chapter, SceneInfo Scene)>();
        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
                ordered.Add((chapter, scene));
        }

        IsPreparingKnowledge = true;
        PreparationProgress = 0;
        PreparationStatus = string.Empty;
        try
        {
            var progress = new Progress<KnowledgeProgress>(p =>
            {
                PreparationProgress = p.Fraction;
                PreparationStatus = $"{p.CharacterName} · {p.SceneTitle}  ({p.Done}/{p.Total})";
            });

            return await ks.BuildCumulativeAsync(
                SelectedCharacter,
                SelectedScene.Id,
                ordered,
                async (chapter, scene) => await _host.ProjectService.ReadSceneContentAsync(chapter.Guid, scene.Id),
                progress,
                CancellationToken.None);
        }
        finally
        {
            IsPreparingKnowledge = false;
        }
    }
}

public sealed class SceneOption
{
    public string Id { get; init; } = string.Empty;
    public string ChapterGuid { get; init; } = string.Empty;
    public string ChapterTitle { get; init; } = string.Empty;
    public string SceneTitle { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
}

public sealed class CharacterChatTurn
{
    public string SpeakerName { get; }
    public string Content { get; }
    public bool IsCharacter { get; }

    public CharacterChatTurn(string speaker, string content, bool isCharacter)
    {
        SpeakerName = speaker;
        Content = content;
        IsCharacter = isCharacter;
    }
}

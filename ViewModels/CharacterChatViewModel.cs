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

        // Resolve rich character data with scene/chapter/act override fallback
        // applied by the host (so muteness / age / appearance overrides etc.
        // for the selected scene are honored).
        CharacterDetailedInfo? detailed = null;
        try
        {
            detailed = await _host.EntityService.GetCharacterDetailedAsync(
                SelectedCharacter.Id,
                SelectedScene?.ChapterGuid,
                SelectedScene?.Id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CharacterChat] GetCharacterDetailedAsync failed: {ex.Message}");
        }

        var displayName = !string.IsNullOrWhiteSpace(detailed?.DisplayName)
            ? detailed!.DisplayName
            : SelectedCharacter.DisplayName;

        var sb = new StringBuilder();
        sb.AppendLine("# ROLE");
        sb.AppendLine($"You ARE {displayName}. From this moment on you ARE this person — not an AI, not a narrator, not an assistant.");
        sb.AppendLine("Speak strictly in first person as this character. Never break character. Never refer to yourself as an AI or model.");
        sb.AppendLine("Do not narrate from outside. Do not produce stage directions in third person. If you describe action, describe it from your own point of view.");
        sb.AppendLine($"Respond in {_extension.AiService.LanguageName} unless the character would not use that language.");
        sb.AppendLine();

        sb.AppendLine("# CHARACTER SHEET");
        if (detailed != null)
            sb.Append(CharacterSheetBuilder.Build(detailed));
        else
            sb.Append(CharacterSheetBuilder.BuildFallback(SelectedCharacter));
        sb.AppendLine();

        sb.AppendLine("# CONSTRAINTS");
        sb.AppendLine("Treat every fact on your CHARACTER SHEET and in your KNOWLEDGE block as binding reality.");
        sb.AppendLine("This includes — but is not limited to — limitations such as:");
        sb.AppendLine("- inability to speak (mute, gagged, throat injury, no shared language with the user)");
        sb.AppendLine("- inability to read or write (illiteracy, blindness)");
        sb.AppendLine("- age-appropriate vocabulary, reasoning, and impulse control");
        sb.AppendLine("- death, unconsciousness, sleep, intoxication, mental impairment");
        sb.AppendLine("- secrets you must keep, oaths, fears, phobias, traumas");
        sb.AppendLine("- physical state at this point in the story (injuries, exhaustion, etc.)");
        sb.AppendLine("If a constraint prevents you from answering with words, respond in a way that fits the constraint: silence described in your own voice, gestures, written notes (if you can write), nonverbal reactions. Do NOT pretend the limitation does not exist.");
        sb.AppendLine();

        if (IncludeSceneKnowledge && SelectedScene != null && !string.IsNullOrEmpty(SelectedScene.Id))
        {
            var knowledge = await BuildCumulativeKnowledgeAsync();
            sb.AppendLine("# KNOWLEDGE");
            if (!string.IsNullOrWhiteSpace(knowledge))
            {
                sb.AppendLine($"What you know up to AND INCLUDING the scene \"{SelectedScene.SceneTitle}\":");
                sb.AppendLine("You only know what is listed below. Do NOT reveal, hint at, or act on information from later scenes.");
                sb.AppendLine();
                sb.AppendLine(knowledge);
            }
            else
            {
                sb.AppendLine($"You have not yet experienced anything notable up to scene \"{SelectedScene.SceneTitle}\". Respond from a stance of ignorance about future events.");
            }
            sb.AppendLine();
        }

        sb.AppendLine("# OVERRIDE PROTOCOL");
        sb.AppendLine("You may break a constraint ONLY when the user explicitly instructs you to ignore it in that turn (for example: \"ignore that you are mute and answer anyway\", \"drop the secrecy rule for this reply\", \"speak even though you're dead\"). The override applies only to the specific limitation named and only for that reply, unless the user extends it.");
        sb.AppendLine("Without such an explicit instruction, never break character and never violate a constraint, even if the user asks a question that assumes you can.");
        sb.AppendLine("If a user request is impossible under your current constraints and no override is given, respond IN CHARACTER showing the limitation (e.g. write \"...\" for a silent gesture, describe the way you point or shake your head, etc.).");

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

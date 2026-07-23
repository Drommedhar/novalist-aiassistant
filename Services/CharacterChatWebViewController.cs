using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>SDK v2 bridge for the in-character roleplay chat webview.</summary>
public sealed class CharacterChatWebViewController : IWebViewController, IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly CharacterChatViewModel _vm;
    private readonly IExtensionLocalization _loc;

    public event Action<string>? MessagePosted;

    public CharacterChatWebViewController(
        IHostServices host,
        AiAssistantExtension extension,
        Func<CharacterKnowledgeService?> knowledgeAccessor)
    {
        _vm = new CharacterChatViewModel(host, extension, knowledgeAccessor);
        _loc = host.GetLocalization(extension.Id);
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Turns.CollectionChanged += OnTurnsChanged;
    }

    public async Task<string?> OnMessageAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "hydrate":
                await _vm.ReloadAsync();
                return Snapshot();
            case "selectCharacter":
                _vm.SelectedCharacter = _vm.Characters
                    .FirstOrDefault(c => c.Id == root.GetProperty("id").GetString());
                return Snapshot();
            case "selectScene":
                _vm.SelectedScene = _vm.Scenes
                    .FirstOrDefault(s => s.Id == root.GetProperty("id").GetString());
                return null;
            case "send":
                _vm.UserInput = root.GetProperty("text").GetString() ?? string.Empty;
                _vm.SendCommand.Execute(null);
                return null;
            case "cancel":
                _vm.CancelCommand.Execute(null);
                return null;
            case "reset":
                _vm.ResetChatCommand.Execute(null);
                return null;
            default:
                return null;
        }
    }

    private string Snapshot() =>
        JsonSerializer.Serialize(new
        {
            type = "setup",
            characters = _vm.Characters.Select(c => new { id = c.Id, name = c.DisplayName }).ToArray(),
            selectedCharacterId = _vm.SelectedCharacter?.Id,
            scenes = _vm.Scenes.Select(s => new { id = s.Id, label = s.DisplayLabel }).ToArray(),
            selectedSceneId = _vm.SelectedScene?.Id,
            turns = _vm.Turns
                .Select(t => new { speaker = t.SpeakerName, text = t.Content, isCharacter = t.IsCharacter })
                .ToArray(),
            // Labels come from the host so the panel follows the project language
            // instead of being hardcoded English.
            strings = new Dictionary<string, string>
            {
                ["send"] = _loc.T("characterChat.send"),
                ["stop"] = _loc.T("characterChat.stop"),
                ["reset"] = _loc.T("characterChat.reset"),
                ["placeholder"] = _loc.T("characterChat.inputWatermark"),
            }
        }, Json);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CharacterChatViewModel.StreamingResponse)
            or nameof(CharacterChatViewModel.IsGenerating)
            or nameof(CharacterChatViewModel.IsPreparingKnowledge)
            or nameof(CharacterChatViewModel.PreparationStatus))
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "state",
                streaming = _vm.StreamingResponse,
                isGenerating = _vm.IsGenerating,
                isPreparing = _vm.IsPreparingKnowledge,
                preparationStatus = _vm.PreparationStatus
            }, Json));
        }
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;
        foreach (CharacterChatTurn turn in e.NewItems)
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "turn",
                speaker = turn.SpeakerName,
                text = turn.Content,
                isCharacter = turn.IsCharacter
            }, Json));
        }
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Turns.CollectionChanged -= OnTurnsChanged;
        _vm.Dispose();
    }
}

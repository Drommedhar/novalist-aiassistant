using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// SDK v2 bridge for the AI chat webview: wraps the existing AiChatViewModel
/// so the web page runs the exact same logic (story-context system prompt,
/// AI hooks, streaming, cancel) as the sidebar did.
/// </summary>
public sealed class ChatWebViewController : IWebViewController, IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AiChatViewModel _vm;
    private readonly IExtensionLocalization _loc;

    public event Action<string>? MessagePosted;

    public ChatWebViewController(IHostServices host, AiAssistantExtension extension)
    {
        _vm = new AiChatViewModel(host, extension);
        _loc = host.GetLocalization(extension.Id);
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Messages.CollectionChanged += OnMessagesChanged;
    }

    public Task<string?> OnMessageAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        var type = document.RootElement.GetProperty("type").GetString();
        switch (type)
        {
            case "send":
                _vm.UserInput = document.RootElement.GetProperty("text").GetString() ?? string.Empty;
                _vm.SendCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "cancel":
                _vm.CancelCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "reset":
                _vm.ClearChatCommand.Execute(null);
                return Task.FromResult<string?>(null);
            case "hydrate":
                return Task.FromResult<string?>(JsonSerializer.Serialize(new
                {
                    type = "history",
                    messages = _vm.Messages
                        .Select(m => new { role = m.Role, text = m.Content, thinking = m.Thinking })
                        .ToArray(),
                    // Labels come from the host so the panel follows the project
                    // language instead of being hardcoded English.
                    strings = new Dictionary<string, string>
                    {
                        ["send"] = _loc.T("ai.send"),
                        ["stop"] = _loc.T("ai.stop"),
                        ["clear"] = _loc.T("ai.clearChat"),
                        ["placeholder"] = _loc.T("ai.chatPlaceholder"),
                    }
                }, Json));
            default:
                return Task.FromResult<string?>(null);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiChatViewModel.StreamingResponse)
            or nameof(AiChatViewModel.StreamingThinking)
            or nameof(AiChatViewModel.IsGenerating))
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "state",
                streaming = _vm.StreamingResponse,
                thinking = _vm.StreamingThinking,
                isGenerating = _vm.IsGenerating
            }, Json));
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;
        foreach (AiChatMessageItem item in e.NewItems)
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "message",
                role = item.Role,
                text = item.Content,
                thinking = item.Thinking
            }, Json));
        }
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Messages.CollectionChanged -= OnMessagesChanged;
        _vm.Dispose();
    }
}

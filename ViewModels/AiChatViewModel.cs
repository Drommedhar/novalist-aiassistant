using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;
using Novalist.Extensions.AiAssistant.Services;

namespace Novalist.Extensions.AiAssistant.ViewModels;

public partial class AiChatViewModel : ObservableObject, IDisposable
{
    private readonly IHostServices _host;
    private readonly AiAssistantExtension _extension;
    private readonly IExtensionLocalization _loc;

    public IExtensionLocalization Loc => _loc;

    [ObservableProperty]
    private string _userInput = string.Empty;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string _streamingResponse = string.Empty;

    [ObservableProperty]
    private string _streamingThinking = string.Empty;

    [ObservableProperty]
    private bool _hasThinking;

    [ObservableProperty]
    private bool _isThinkingExpanded;

    public ObservableCollection<AiChatMessageItem> Messages { get; } = [];
    internal IReadOnlyList<ContextBlock> SelectedContext { get; set; } = [];

    internal ChatRequestPlan Preview(string text, IReadOnlyList<ContextBlock> context)
        => ChatRequestAssembler.Build(BuildSystemPrompt(), _conversationHistory, text, context,
            _extension.Settings.ContextLength > 0
                ? Math.Min(_extension.ContextEngine.TokenBudget, _extension.Settings.ContextLength)
                : _extension.ContextEngine.TokenBudget);

    private readonly List<AiChatMessage> _conversationHistory = [];
    private CancellationTokenSource? _cts;
    private long _conversationEpoch;

    public AiChatViewModel(IHostServices host, AiAssistantExtension extension)
    {
        _host = host;
        _extension = extension;
        _loc = host.GetLocalization(extension.Id);
        _host.ProjectLoaded += OnProjectLoaded;
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.ProjectLoaded -= OnProjectLoaded;
        _conversationEpoch++;
        _cts?.Cancel();
        _cts = null;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => SendPreparedAsync(UserInput.Trim());

    internal async Task SendPreparedAsync(string text, ChatRequestPlan? request = null)
    {
        if (_disposed || IsGenerating || string.IsNullOrEmpty(text)) return;
        var epoch = _conversationEpoch;
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        IsGenerating = true;

        ResetStreaming();
        var response = new StringBuilder();
        var thinking = new StringBuilder();
        var receivedResponse = false;
        bool IsCurrent() => epoch == _conversationEpoch && !_disposed && ReferenceEquals(_cts, cancellation);

        try
        {
            request ??= Preview(text, SelectedContext);
            if (!IsCurrent()) return;
            UserInput = string.Empty;
            if (!IsCurrent()) return;
            Messages.Add(new AiChatMessageItem("user", text));
            if (!IsCurrent()) return;
            _conversationHistory.Add(new AiChatMessage { Role = "user", Content = text });
            var aiHooks = _host.GetAiHooks();
            if (!IsCurrent()) return;
            var result = await _extension.AiService.GenerateChatWithOutputLimitAsync(
                request.Messages, request.OutputTokenReserve,
                chunk =>
                {
                    if (!IsCurrent()) return;
                    chunk = FilterResponse(chunk, aiHooks);
                    receivedResponse = true;
                    response.Append(chunk);
                    _host.PostToUI(() =>
                    {
                        if (IsCurrent()) StreamingResponse += chunk;
                    });
                },
                null,
                chunk =>
                {
                    if (!IsCurrent()) return;
                    thinking.Append(chunk);
                    _host.PostToUI(() =>
                    {
                        if (!IsCurrent()) return;
                        StreamingThinking += chunk;
                        HasThinking = true;
                    });
                },
                cancellation.Token);

            if (!IsCurrent()) return;

            var answer = receivedResponse ? response.ToString() : FilterResponse(result.Response, aiHooks);
            if (!IsCurrent()) return;
            _conversationHistory.Add(new AiChatMessage { Role = "assistant", Content = answer });

            Messages.Add(new AiChatMessageItem("assistant", answer, result.Thinking));
        }
        catch (OperationCanceledException)
        {
            if (!IsCurrent()) return;
            if (response.Length > 0)
            {
                Messages.Add(new AiChatMessageItem("assistant", response + " [cancelled]", thinking.ToString()));
                _conversationHistory.Add(new AiChatMessage { Role = "assistant", Content = response.ToString() });
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent())
                Messages.Add(new AiChatMessageItem("error", ex.Message));
        }
        finally
        {
            if (IsCurrent())
            {
                _cts = null;
                ResetStreaming();
                IsGenerating = false;
            }
        }
    }

    private static string FilterResponse(string chunk, IReadOnlyList<Novalist.Sdk.Hooks.IAiHook> hooks)
    {
        foreach (var hook in hooks)
        {
            try { chunk = hook.OnResponseChunk(chunk); }
            catch { /* A failed hook must not discard the provider response. */ }
        }
        return chunk;
    }

    private void ResetStreaming()
    {
        StreamingResponse = string.Empty;
        StreamingThinking = string.Empty;
        HasThinking = false;
        IsThinkingExpanded = false;
    }

    private bool CanSend() => !IsGenerating && !string.IsNullOrWhiteSpace(UserInput);

    partial void OnUserInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsGeneratingChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task ClearChatAsync()
    {
        ResetConversation();
        await _extension.AiService.ResetChatSessionAsync();
    }

    private void OnProjectLoaded(ProjectInfo project) => _host.PostToUI(ResetConversation);

    private void ResetConversation()
    {
        _conversationEpoch++;
        _cts?.Cancel();
        _cts = null;
        Messages.Clear();
        _conversationHistory.Clear();
        ResetStreaming();
        IsGenerating = false;
        UserInput = string.Empty;
        SelectedContext = [];
    }

    private string BuildSystemPrompt()
    {
        var settings = _extension.Settings;
        var template = !string.IsNullOrWhiteSpace(settings.SystemPrompt)
            ? settings.SystemPrompt
            : AiSettings.DefaultSystemPrompt;

        var langName = _host.CurrentLanguageDisplayName;
        var prompt = new StringBuilder(template.Replace("{{LANGUAGE}}", langName));

        // Extension AI hooks
        var aiHooks = _host.GetAiHooks();
        if (aiHooks.Count > 0)
        {
            var aiContext = new AiPromptContext
            {
                Language = _host.CurrentLanguage
            };

            foreach (var hook in aiHooks)
            {
                try
                {
                    var addition = hook.OnBuildSystemPrompt(aiContext);
                    if (!string.IsNullOrWhiteSpace(addition))
                        prompt.Append("\n\n").Append(addition);
                }
                catch { /* swallow — never let an extension break AI */ }
            }
        }

        return prompt.ToString();
    }

    internal async Task<List<EntitySummary>> CollectEntitySummariesAsync()
    {
        var summaries = new List<EntitySummary>();
        try
        {
            var characters = await _host.EntityService.LoadCharactersAsync();
            foreach (var c in characters)
            {
                var details = new StringBuilder();
                if (!string.IsNullOrEmpty(c.Role)) details.Append($"Role: {c.Role}. ");
                summaries.Add(new EntitySummary { Id = c.Id, Name = c.DisplayName, Type = "character", Details = details.ToString().TrimEnd() });
            }

            var locations = await _host.EntityService.LoadLocationsAsync();
            foreach (var l in locations)
            {
                var details = new StringBuilder();
                if (!string.IsNullOrEmpty(l.Type)) details.Append($"Type: {l.Type}. ");
                summaries.Add(new EntitySummary { Id = l.Id, Name = l.Name, Type = "location", Details = details.ToString().TrimEnd() });
            }

            var items = await _host.EntityService.LoadItemsAsync();
            foreach (var it in items)
            {
                var details = new StringBuilder();
                if (!string.IsNullOrEmpty(it.Type)) details.Append($"Type: {it.Type}. ");
                summaries.Add(new EntitySummary { Id = it.Id, Name = it.Name, Type = "item", Details = details.ToString().TrimEnd() });
            }

            var lore = await _host.EntityService.LoadLoreAsync();
            foreach (var lr in lore)
            {
                var details = new StringBuilder();
                if (!string.IsNullOrEmpty(lr.Category)) details.Append($"Category: {lr.Category}. ");
                summaries.Add(new EntitySummary { Id = lr.Id, Name = lr.Name, Type = "lore", Details = details.ToString().TrimEnd() });
            }

            // Collect custom entity types and their entities
            var customTypes = _host.EntityService.GetCustomEntityTypes();
            foreach (var ct in customTypes)
            {
                try
                {
                    var customEntities = await _host.EntityService.LoadCustomEntitiesAsync(ct.TypeKey);
                    foreach (var ce in customEntities)
                    {
                        var ceDetails = new StringBuilder();
                        foreach (var field in ce.Fields.Take(3))
                            ceDetails.Append($"{field.Key}: {field.Value}. ");
                        summaries.Add(new EntitySummary { Id = ce.Id, Name = ce.Name, Type = $"custom:{ct.DisplayName}", Details = ceDetails.ToString().TrimEnd() });
                    }
                }
                catch { /* skip inaccessible custom type */ }
            }
        }
        catch
        {
            // Best-effort entity collection
        }
        return summaries;
    }
}

public sealed class AiChatMessageItem
{
    public AiChatMessageItem(string role, string content, string? thinking = null)
    {
        Role = role;
        Content = content;
        Thinking = thinking ?? string.Empty;
        HasThinking = !string.IsNullOrWhiteSpace(thinking);
        IsUser = string.Equals(role, "user", StringComparison.Ordinal);
        IsAssistant = string.Equals(role, "assistant", StringComparison.Ordinal);
        IsError = string.Equals(role, "error", StringComparison.Ordinal);
    }

    public string Role { get; }
    public string Content { get; }
    public string Thinking { get; }
    public bool HasThinking { get; }
    public bool IsUser { get; }
    public bool IsAssistant { get; }
    public bool IsError { get; }
}

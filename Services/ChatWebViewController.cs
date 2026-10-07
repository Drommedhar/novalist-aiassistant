using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Reads selected context and forwards chat state to the extension webview.
/// </summary>
public sealed class ChatWebViewController : IWebViewController, IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AiChatViewModel _vm;
    private readonly IExtensionLocalization _loc;

    public event Action<string>? MessagePosted;

    private readonly IHostServices _host;
    private readonly AiAssistantExtension _extension;
    private readonly ChatStreamUpdate _stream = new();
    private int _generation;
    private int _disposed;

    public ChatWebViewController(IHostServices host, AiAssistantExtension extension)
    {
        _host = host;
        _extension = extension;
        _vm = new AiChatViewModel(host, extension);
        _loc = host.GetLocalization(extension.Id);
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _host.ProjectLoaded += OnProjectLoaded;
    }

    public async Task<string?> OnMessageAsync(string json)
    {
        var generation = Volatile.Read(ref _generation);
        if (!IsCurrent(generation)) return null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString();
        try
        {
            switch (type)
            {
                case "send": return await SendAsync(root, generation);
                case "preview": return await PreviewAsync(root, generation);
                case "models": return await ModelsAsync(generation);
                case "contextOptions": return await ContextOptionsAsync(generation);
                case "setModel":
                    _extension.AiService.ModelOverride = Text(root, "model");
                    return null;
                case "cancel":
                    Interlocked.Increment(ref _generation);
                    _vm.CancelCommand.Execute(null);
                    return null;
                case "reset":
                    Interlocked.Increment(ref _generation);
                    await _vm.ClearChatCommand.ExecuteAsync(null);
                    return null;
                case "hydrate": return History();
                default: return null;
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(generation))
        {
            return null;
        }
        catch (Exception error) when ((type is "send" or "preview") && (error is IOException or InvalidOperationException))
        {
            return JsonSerializer.Serialize(new
            {
                type = type == "send" ? "sendRejected" : "previewRejected",
                text = Text(root, "text"), error = error.Message
            }, Json);
        }
    }

    private async Task<string?> SendAsync(JsonElement message, int generation)
    {
        var text = Text(message, "text").Trim();
        if (_vm.IsGenerating || text.Length == 0)
            return JsonSerializer.Serialize(new { type = "sendRejected", text }, Json);
        var blocks = await ContextBlocksAsync(message, generation);
        RequireCurrent(generation);
        if (_vm.IsGenerating)
            return JsonSerializer.Serialize(new { type = "sendRejected", text }, Json);
        ChatRequestPlan request;
        try { request = _vm.Preview(text, blocks); }
        catch (InvalidOperationException exception)
        {
            return JsonSerializer.Serialize(new { type = "sendRejected", text, error = exception.Message }, Json);
        }
        RequireCurrent(generation);
        if (request.Dropped.Count > 0)
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "notice",
                text = _loc.T("context.dropped").Replace("{0}", string.Join(", ", request.Dropped))
            }, Json));
        RequireCurrent(generation);
        _vm.UserInput = text;
        _ = _vm.SendPreparedAsync(text, request);
        return JsonSerializer.Serialize(new { type = "sendAccepted" }, Json);
    }

    private async Task<string?> PreviewAsync(JsonElement message, int generation)
    {
        var blocks = await ContextBlocksAsync(message, generation);
        RequireCurrent(generation);
        var request = _vm.Preview(Text(message, "text"), blocks);
        RequireCurrent(generation);
        var preview = request.Preview;
        return JsonSerializer.Serialize(new
        {
            type = "preview", text = preview.Text, estimatedTokens = preview.EstimatedTokens,
            tokenBudget = preview.TokenBudget, outputTokenReserve = request.OutputTokenReserve,
            dropped = request.Dropped, lines = preview.Lines,
            strings = PreviewStrings()
        }, Json);
    }

    private Dictionary<string, string> PreviewStrings() => new()
    {
        ["previewTitle"] = _loc.T("ai.previewTitle"),
        ["previewHint"] = _loc.T("ai.previewHint"),
        ["previewBlock"] = _loc.T("ai.previewBlock"),
        ["previewTier"] = _loc.T("ai.previewTier"),
        ["previewTokens"] = _loc.T("ai.previewTokens"),
        ["previewKept"] = _loc.T("ai.previewKept"),
        ["previewDropped"] = _loc.T("ai.previewDropped"),
        ["previewTotal"] = _loc.T("ai.previewTotal"),
        ["previewReservedOutput"] = _loc.T("ai.previewReservedOutput"),
        ["previewEmpty"] = _loc.T("ai.previewEmpty"),
        ["previewClose"] = _loc.T("ai.previewClose")
    };

    private async Task<string?> ModelsAsync(int generation)
    {
        var models = await _extension.AiService.ListModelsAsync();
        RequireCurrent(generation);
        return JsonSerializer.Serialize(new
        {
            type = "models", models = models.Select(model => new { id = model.Key, label = model.DisplayName }),
            current = _extension.AiService.ModelOverride,
            strings = new Dictionary<string, string>
            {
                ["modelLabel"] = _loc.T("ai.modelLabel"),
                ["modelFromSettings"] = _loc.T("ai.modelFromSettings")
            }
        }, Json);
    }

    private string History() => JsonSerializer.Serialize(new
    {
        type = "history",
        isGenerating = _vm.IsGenerating,
        streamingResponse = _vm.StreamingResponse,
        streamingThinking = _vm.StreamingThinking,
        messages = _vm.Messages.Select(message => new
            { role = message.Role, text = message.Content, thinking = message.Thinking }).ToArray(),
        strings = new Dictionary<string, string>
        {
            ["send"] = _loc.T("ai.send"), ["stop"] = _loc.T("ai.stop"),
            ["clear"] = _loc.T("ai.clearChat"), ["placeholder"] = _loc.T("ai.chatPlaceholder")
        }
    }, Json);

    private async Task<string?> ContextOptionsAsync(int generation)
    {
        var groups = new List<object>();
        var current = _host.ProjectService.CurrentScene;
        var sceneItems = new List<object>();
        if (current != null)
        {
            sceneItems.Add(new { id = "scene:" + current.Id, label = current.Title });
            sceneItems.Add(new { id = "chapter:" + current.ChapterGuid, label = current.ChapterTitle });
        }
        sceneItems.Add(new { id = "outline", label = _loc.T("ai.contextOutline") });
        groups.Add(new { title = _loc.T("ai.contextWhereIAm"), items = sceneItems });
        var characters = await _host.EntityService.LoadCharactersAsync();
        RequireCurrent(generation);
        var items = characters.Where(character => !string.IsNullOrWhiteSpace(character.DisplayName))
            .Select(character => new { id = "entity:" + character.Id, label = character.DisplayName }).ToList();
        if (items.Count > 0) groups.Add(new { title = _loc.T("ai.contextCast"), items });
        return JsonSerializer.Serialize(new { type = "contextOptions", groups }, Json);
    }

    private async Task<List<ContextBlock>> ContextBlocksAsync(JsonElement message, int generation)
    {
        var blocks = new List<ContextBlock>();
        if (!message.TryGetProperty("include", out var include) || include.ValueKind != JsonValueKind.Array)
            return blocks;
        var wanted = include.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return blocks;
        var current = _host.ProjectService.CurrentScene;
        if (current != null && wanted.Contains("scene:" + current.Id))
        {
            var prose = await _host.ProjectService.ReadSceneContentAsync(current.ChapterGuid, current.Id);
            RequireCurrent(generation);
            blocks.Add(new ContextBlock(ContextTier.Subject, _loc.T("ai.contextThisScene"), Strip(prose)));
        }
        if (current != null && wanted.Contains("chapter:" + current.ChapterGuid))
        {
            var chapter = await ReadChapterAsync(current, generation);
            if (chapter.Length > 0)
                blocks.Add(new ContextBlock(ContextTier.Preceding, _loc.T("ai.contextThisChapter"), chapter));
        }
        if (wanted.Contains("outline"))
            blocks.Add(new ContextBlock(ContextTier.Background, _loc.T("ai.contextOutline"), await ReadOutlineAsync(generation)));
        var entityIds = wanted.Where(id => id.StartsWith("entity:", StringComparison.Ordinal))
            .Select(id => id["entity:".Length..]).ToHashSet(StringComparer.Ordinal);
        if (entityIds.Count > 0 && current != null)
            blocks.AddRange(await ReadEntitiesAsync(current, entityIds, generation));
        return blocks;
    }

    private async Task<string> ReadChapterAsync(SceneInfo current, int generation)
    {
        var builder = new StringBuilder();
        foreach (var scene in _host.ProjectService.GetScenesForChapter(current.ChapterGuid))
        {
            if (scene.Id == current.Id) continue;
            var prose = await _host.ProjectService.ReadSceneContentAsync(current.ChapterGuid, scene.Id);
            RequireCurrent(generation);
            builder.Append(scene.Title).Append(":\n").AppendLine(Strip(prose)).AppendLine();
        }
        return builder.ToString();
    }

    private async Task<string> ReadOutlineAsync(int generation)
    {
        var builder = new StringBuilder();
        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            builder.Append("- ").AppendLine(chapter.Title);
            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                var synopsis = await _host.ProjectService.GetSceneSynopsisAsync(chapter.Guid, scene.Id);
                RequireCurrent(generation);
                builder.Append("  - ").Append(scene.Title);
                if (!string.IsNullOrWhiteSpace(synopsis)) builder.Append(": ").Append(synopsis);
                builder.AppendLine();
            }
        }
        return builder.ToString();
    }

    private async Task<List<ContextBlock>> ReadEntitiesAsync(SceneInfo current, HashSet<string> entityIds, int generation)
    {
        var allowed = await _host.EntityService.GetAiContextAsync(current.ChapterGuid, current.Id);
        RequireCurrent(generation);
        var blocks = new List<ContextBlock>();
        foreach (var entry in allowed.Where(entry => entityIds.Contains(entry.Id)))
        {
            var builder = new StringBuilder();
            foreach (var section in entry.Sections)
            {
                if (!string.IsNullOrWhiteSpace(section.Content))
                    builder.Append(section.Title).Append(": ").AppendLine(Strip(section.Content));
            }
            blocks.Add(new ContextBlock(ContextTier.Entities, entry.Name, builder.ToString(), 100));
        }
        return blocks;
    }

    private static string Text(JsonElement message, string property)
        => message.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private bool IsCurrent(int generation)
        => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _generation) == generation;

    private void RequireCurrent(int generation)
    {
        if (!IsCurrent(generation)) throw new OperationCanceledException("The chat context changed while it was loading.");
    }

    private void OnProjectLoaded(ProjectInfo project) => Interlocked.Increment(ref _generation);

    private static string Strip(string html)
    {
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(
            html ?? string.Empty, @"</p\s*>|<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            withBreaks, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(stripped).Trim();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (e.PropertyName is nameof(AiChatViewModel.StreamingResponse)
            or nameof(AiChatViewModel.StreamingThinking)
            or nameof(AiChatViewModel.IsGenerating))
        {
            var delta = _stream.Next(_vm.StreamingResponse, _vm.StreamingThinking);
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "state",
                delta.StreamingDelta,
                delta.ThinkingDelta,
                delta.Reset,
                isGenerating = _vm.IsGenerating
            }, Json));
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new { type = "history", reset = true, messages = Array.Empty<object>() }, Json));
            return;
        }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Increment(ref _generation);
        _host.ProjectLoaded -= OnProjectLoaded;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Messages.CollectionChanged -= OnMessagesChanged;
        _vm.Dispose();
    }
}

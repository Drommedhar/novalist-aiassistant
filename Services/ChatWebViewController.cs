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

    private readonly IHostServices _host;
    private readonly AiAssistantExtension _extension;

    public ChatWebViewController(IHostServices host, AiAssistantExtension extension)
    {
        _host = host;
        _extension = extension;
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
            {
                var text = document.RootElement.GetProperty("text").GetString() ?? string.Empty;

                // Anything the writer ticked in the picker goes in front of what
                // they typed. Guessing what a chat needs from the project is the
                // thing this replaces: a picker means the writer can see exactly
                // what was sent, and a wrong answer is theirs to fix rather than
                // a mystery.
                if (document.RootElement.TryGetProperty("include", out var include)
                    && include.ValueKind == JsonValueKind.Array)
                {
                    var context = BuildContext(include);
                    if (context.Length > 0) text = context + "\n\n" + text;
                }

                _vm.UserInput = text;
                _vm.SendCommand.Execute(null);
                return Task.FromResult<string?>(null);
            }
            case "preview":
            {
                // Exactly what "send" would assemble, before a token is spent
                // on it. The prompt was built and sent in one step and nothing
                // showed what went, so a writer who ticked six characters and
                // got an answer that ignored two could not find out why.
                var blocks = document.RootElement.TryGetProperty("include", out var picked)
                             && picked.ValueKind == JsonValueKind.Array
                    ? ContextBlocks(picked)
                    : [];
                var preview = _extension.ContextEngine.Preview(blocks);
                return Task.FromResult<string?>(JsonSerializer.Serialize(new
                {
                    type = "preview",
                    text = preview.Text,
                    estimatedTokens = preview.EstimatedTokens,
                    tokenBudget = preview.TokenBudget,
                    lines = preview.Lines,
                    strings = new Dictionary<string, string>
                    {
                        ["previewTitle"] = _loc.T("ai.previewTitle"),
                        ["previewHint"] = _loc.T("ai.previewHint"),
                        ["previewBlock"] = _loc.T("ai.previewBlock"),
                        ["previewTier"] = _loc.T("ai.previewTier"),
                        ["previewTokens"] = _loc.T("ai.previewTokens"),
                        ["previewKept"] = _loc.T("ai.previewKept"),
                        ["previewDropped"] = _loc.T("ai.previewDropped"),
                        ["previewTotal"] = _loc.T("ai.previewTotal"),
                        ["previewEmpty"] = _loc.T("ai.previewEmpty"),
                        ["previewClose"] = _loc.T("ai.previewClose")
                    }
                }, Json));
            }
            case "contextOptions":
                return Task.FromResult<string?>(JsonSerializer.Serialize(
                    new { type = "contextOptions", groups = ContextOptions() }, Json));
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

    /// <summary>
    /// What the writer can choose to send.
    ///
    /// Named rather than described: "the scene I am in" and "Mira Vance" are
    /// things somebody can decide about, whereas "relevant context" is not.
    /// </summary>
    private object[] ContextOptions()
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

        try
        {
            var characters = _host.EntityService.LoadCharactersAsync()
                .GetAwaiter().GetResult()
                .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
                .Select(c => new { id = "entity:" + c.Id, label = c.DisplayName })
                .Cast<object>()
                .ToList();
            if (characters.Count > 0)
                groups.Add(new { title = _loc.T("ai.contextCast"), items = characters });
        }
        catch (Exception)
        {
            // No project open, or a Codex that will not load. The scene options
            // are still worth offering.
        }

        return [.. groups];
    }

    /// <summary>
    /// Assembles what was ticked, through the context engine so the order and the
    /// budget are the same here as everywhere else in the extension.
    /// </summary>
    /// <summary>
    /// The assembled context, as a string, for the send path.
    ///
    /// Shares its blocks with the preview: a preview that showed something
    /// other than what is sent would be worse than no preview at all.
    /// </summary>
    private string BuildContext(JsonElement include)
    {
        var assembled = _extension.ContextEngine.Assemble(ContextBlocks(include));

        // What did not fit is said rather than silently dropped. A writer who
        // ticked six characters and got four needs to know which.
        if (assembled.Dropped.Count > 0)
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "notice",
                text = _loc.T("context.dropped").Replace("{0}", string.Join(", ", assembled.Dropped))
            }, Json));

        return assembled.Text;
    }

    private List<ContextBlock> ContextBlocks(JsonElement include)
    {
        var wanted = include.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var blocks = new List<ContextBlock>();
        if (wanted.Count == 0) return blocks;

        var current = _host.ProjectService.CurrentScene;

        if (current != null && wanted.Contains("scene:" + current.Id))
        {
            var prose = _host.ProjectService
                .ReadSceneContentAsync(current.ChapterGuid, current.Id)
                .GetAwaiter().GetResult();
            blocks.Add(new ContextBlock(
                ContextTier.Subject, _loc.T("ai.contextThisScene"), Strip(prose)));
        }

        if (current != null && wanted.Contains("chapter:" + current.ChapterGuid))
        {
            var builder = new StringBuilder();
            foreach (var scene in _host.ProjectService.GetScenesForChapter(current.ChapterGuid))
            {
                if (scene.Id == current.Id) continue;
                var prose = _host.ProjectService
                    .ReadSceneContentAsync(current.ChapterGuid, scene.Id)
                    .GetAwaiter().GetResult();
                builder.Append(scene.Title).Append(":\n").AppendLine(Strip(prose)).AppendLine();
            }
            if (builder.Length > 0)
                blocks.Add(new ContextBlock(
                    ContextTier.Preceding, _loc.T("ai.contextThisChapter"), builder.ToString()));
        }

        if (wanted.Contains("outline"))
        {
            var builder = new StringBuilder();
            foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
            {
                builder.Append("- ").AppendLine(chapter.Title);
                foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
                {
                    var synopsis = _host.ProjectService
                        .GetSceneSynopsisAsync(chapter.Guid, scene.Id)
                        .GetAwaiter().GetResult();
                    builder.Append("  - ").Append(scene.Title);
                    if (!string.IsNullOrWhiteSpace(synopsis))
                        builder.Append(": ").Append(synopsis);
                    builder.AppendLine();
                }
            }
            blocks.Add(new ContextBlock(
                ContextTier.Background, _loc.T("ai.contextOutline"), builder.ToString()));
        }

        var entityIds = wanted
            .Where(w => w.StartsWith("entity:", StringComparison.Ordinal))
            .Select(w => w["entity:".Length..])
            .ToHashSet(StringComparer.Ordinal);

        if (entityIds.Count > 0)
        {
            foreach (var id in entityIds)
            {
                var detailed = _host.EntityService
                    .GetCharacterDetailedAsync(id, current?.ChapterGuid, current?.Id)
                    .GetAwaiter().GetResult();
                if (detailed == null) continue;

                var builder = new StringBuilder();
                foreach (var section in detailed.Sections)
                {
                    if (string.IsNullOrWhiteSpace(section.Content)) continue;
                    builder.Append(section.Title).Append(": ").AppendLine(Strip(section.Content));
                }
                blocks.Add(new ContextBlock(
                    ContextTier.Entities, detailed.DisplayName, builder.ToString(), 100));
            }
        }

        return blocks;
    }

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

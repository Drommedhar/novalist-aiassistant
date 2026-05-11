using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Implements <see cref="IInlineActionContributor"/> for selection-driven AI
/// actions: rewrite / expand / shorten / describe / show-don't-tell / brainstorm.
/// </summary>
public sealed class InlineRewriteService : IInlineActionContributor
{
    private readonly AiService _ai;
    private readonly IExtensionLocalization _loc;
    private readonly IHostServices _host;

    public InlineRewriteService(AiService ai, IHostServices host, IExtensionLocalization loc)
    {
        _ai = ai;
        _host = host;
        _loc = loc;
    }

    private const string Group = "AI";

    public IReadOnlyList<InlineActionDescriptor> GetInlineActions() =>
    [
        new() { Id = "ai.rewrite",       Label = _loc.T("inline.rewrite"),       Group = Group, Priority = 10 },
        new() { Id = "ai.expand",        Label = _loc.T("inline.expand"),        Group = Group, Priority = 20 },
        new() { Id = "ai.shorten",       Label = _loc.T("inline.shorten"),       Group = Group, Priority = 30 },
        new() { Id = "ai.describe",      Label = _loc.T("inline.describe"),      Group = Group, Priority = 40 },
        new() { Id = "ai.showdonttell",  Label = _loc.T("inline.showDontTell"),  Group = Group, Priority = 50 },
        new() { Id = "ai.brainstorm",    Label = _loc.T("inline.brainstorm"),    Group = Group, Priority = 60 },
        new() { Id = "ai.brainstorm3",   Label = _loc.T("inline.brainstorm3"),   Group = Group, Priority = 65 },
    ];

    public async Task<InlineActionResult> ExecuteAsync(string actionId, InlineActionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SelectedText))
            return new InlineActionResult { Error = _loc.T("inline.noSelection") };

        var (sys, disposition) = BuildSystem(actionId);
        if (sys == null)
            return new InlineActionResult { Error = $"Unknown action: {actionId}" };

        var user = $"TEXT:\n{request.SelectedText}";

        // Brainstorm actions enrich the user message with preceding-scene context
        // and a character roster so continuations stay consistent with what came
        // before.
        if (actionId is "ai.brainstorm" or "ai.brainstorm3")
        {
            var ctx = await BuildBrainstormContextAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ctx))
                user = ctx + "\n\n" + user;
        }

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = sys },
            new() { Role = "user", Content = user },
        };

        try
        {
            var result = await _ai.GenerateChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            var text = (result.Response ?? string.Empty).Trim();
            text = StripCodeFences(text);
            if (string.IsNullOrEmpty(text))
                return new InlineActionResult { Error = _loc.T("inline.emptyResult") };
            return new InlineActionResult { Text = text, Disposition = disposition };
        }
        catch (Exception ex)
        {
            return new InlineActionResult { Error = ex.Message };
        }
    }

    private (string? system, InlineActionDisposition disposition) BuildSystem(string actionId)
    {
        var lang = _ai.LanguageName;
        return actionId switch
        {
            "ai.rewrite" => ($"You rewrite a passage from a novel. Preserve meaning, point of view, and tense. Improve clarity, rhythm, and word choice. Match the existing voice. Output only the rewritten text — no preamble, no quotes, no commentary. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.expand" => ($"You expand a passage from a novel with additional sensory detail, atmosphere, and beat-level character motion. Stay in the same point of view, tense, and voice. Do not invent plot facts that contradict the passage. Output only the expanded text. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.shorten" => ($"You tighten a passage from a novel. Cut filler, redundancy, and weak modifiers. Preserve meaning, point of view, and tense. Output only the shortened text. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.describe" => ($"You turn the user's input — typically a brief noun phrase or scene seed — into a vivid prose description suitable for a novel. Stay in the surrounding tense and point of view if discernible. Output only the description. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.showdonttell" => ($"You convert telling into showing. The user has given a passage that states emotions, traits, or events flatly. Rewrite it to dramatize those facts through concrete sensory detail, action, and dialogue, without naming the underlying emotion or trait. Preserve point of view and tense. Output only the rewritten passage. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.brainstorm" => ($"You write a single short continuation (1–3 sentences) that could follow the user's passage in the next beat of a novel. Match the voice, point of view, and tense. Use the surrounding scene context and character roster to stay consistent. Do not summarize. Output only the continuation text. Respond in {lang}.", InlineActionDisposition.InsertAfterSelection),
            "ai.brainstorm3" => ($"You propose three distinct continuations that could follow the user's passage in the next beat of a novel. Each option should be 1–3 sentences, divergent from the others (different action, tone, or stakes), and consistent with the voice, point of view, tense, scene context, and character roster supplied. Output exactly three numbered options as plain text:\n1. ...\n2. ...\n3. ...\nNo preamble, no commentary. Respond in {lang}.", InlineActionDisposition.InsertAfterSelection),
            _ => (null, InlineActionDisposition.ReplaceSelection),
        };
    }

    private async Task<string> BuildBrainstormContextAsync(InlineActionRequest request, CancellationToken cancellationToken)
    {
        var sb = new System.Text.StringBuilder();

        // Character roster (names + roles) so continuations don't invent people.
        try
        {
            var chars = await _host.EntityService.LoadCharactersAsync().ConfigureAwait(false);
            if (chars.Count > 0)
            {
                sb.AppendLine("CHARACTER ROSTER:");
                foreach (var c in chars)
                {
                    if (string.IsNullOrWhiteSpace(c.DisplayName)) continue;
                    var role = string.IsNullOrWhiteSpace(c.Role) ? string.Empty : $" — {c.Role}";
                    sb.Append("- ").Append(c.DisplayName).AppendLine(role);
                }
                sb.AppendLine();
            }
        }
        catch { }

        // Preceding scenes: up to two scenes immediately before SceneId, using
        // stored synopsis when available else trimmed plain text.
        if (!string.IsNullOrEmpty(request.SceneId) && !string.IsNullOrEmpty(request.ChapterGuid))
        {
            try
            {
                var ordered = new List<(string ChapterGuid, string ChapterTitle, string SceneId, string SceneTitle)>();
                foreach (var ch in _host.ProjectService.GetChaptersOrdered())
                    foreach (var sc in _host.ProjectService.GetScenesForChapter(ch.Guid))
                        ordered.Add((ch.Guid, ch.Title, sc.Id, sc.Title));

                var idx = ordered.FindIndex(x => string.Equals(x.SceneId, request.SceneId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    int start = Math.Max(0, idx - 2);
                    var preceding = new List<string>();
                    for (int i = start; i < idx; i++)
                    {
                        var (cg, ct, sid, st) = ordered[i];
                        string summary = await _host.ProjectService.GetSceneSynopsisAsync(cg, sid).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(summary))
                        {
                            var raw = await _host.ProjectService.ReadSceneContentAsync(cg, sid).ConfigureAwait(false);
                            summary = TrimPreceding(StripHtml(raw), 600);
                        }
                        if (!string.IsNullOrWhiteSpace(summary))
                            preceding.Add($"[{ct} / {st}] {summary}");
                    }

                    if (preceding.Count > 0)
                    {
                        sb.AppendLine("PRECEDING SCENES (most recent last):");
                        foreach (var p in preceding) sb.AppendLine("- " + p);
                        sb.AppendLine();
                    }
                }
            }
            catch { }
        }

        return sb.ToString().Trim();
    }

    private static string TrimPreceding(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Trim();
        return text.Length <= max ? text : "…" + text[^max..];
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) sb.Append(ch);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var nl = trimmed.IndexOf('\n');
            if (nl > 0) trimmed = trimmed[(nl + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }
        return trimmed;
    }
}

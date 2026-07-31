using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly Func<IReadOnlyList<SavedPrompt>> _prompts;

    /// <summary>
    /// The writer's own voice, applied to every prompt below. Set after
    /// construction because the profile store outlives any one action and is
    /// read fresh: a profile built this afternoon should reach the next rewrite,
    /// not the next restart.
    /// </summary>
    public StyleProfileService? StyleProfiles { get; set; }

    /// <param name="prompts">
    /// The writer's own prompts, read fresh each time the menu is built so an
    /// edit shows up without restarting anything.
    /// </param>
    public InlineRewriteService(
        AiService ai,
        IHostServices host,
        IExtensionLocalization loc,
        Func<IReadOnlyList<SavedPrompt>>? prompts = null)
    {
        _ai = ai;
        _host = host;
        _loc = loc;
        _prompts = prompts ?? (() => []);
    }

    /// <summary>Prefix that marks an action as coming from the writer's library.</summary>
    private const string CustomPrefix = "ai.custom.";

    private const string Group = "AI";

    public IReadOnlyList<InlineActionDescriptor> GetInlineActions() =>
    [
        new() { Id = "ai.rewrite",       Label = _loc.T("inline.rewrite"),       Group = Group, Priority = 10 },
        new() { Id = "ai.rewrite3",      Label = _loc.T("inline.rewrite3"),      Group = Group, Priority = 15 },
        new() { Id = "ai.expand",        Label = _loc.T("inline.expand"),        Group = Group, Priority = 20 },
        new() { Id = "ai.shorten",       Label = _loc.T("inline.shorten"),       Group = Group, Priority = 30 },
        new() { Id = "ai.describe",      Label = _loc.T("inline.describe"),      Group = Group, Priority = 40 },
        new() { Id = "ai.showdonttell",  Label = _loc.T("inline.showDontTell"),  Group = Group, Priority = 50 },
        new() { Id = "ai.brainstorm",    Label = _loc.T("inline.brainstorm"),    Group = Group, Priority = 60 },
        new() { Id = "ai.brainstorm3",   Label = _loc.T("inline.brainstorm3"),   Group = Group, Priority = 65 },

        // These two work from a bare caret rather than a selection, which is
        // the point: the writer has stopped mid-scene with nothing highlighted.
        // Reached from the slash menu as /continue and /beat.
        new()
        {
            Id = "ai.continue",
            Label = _loc.T("inline.continue"),
            Group = Group,
            Priority = 5,
            AllowsEmptySelection = true,
            SlashKeyword = "continue",
        },
        new()
        {
            Id = "ai.beat",
            Label = _loc.T("inline.beat"),
            Group = Group,
            Priority = 6,
            AllowsEmptySelection = true,
            SlashKeyword = "beat",
        },

        // The writer's own, in the same menu as ours. Every built-in prompt here
        // is somebody's opinion about what to say to a model, and that opinion is
        // often wrong for a particular book.
        .. _prompts().Select(p => new InlineActionDescriptor
        {
            Id = CustomPrefix + p.Id,
            Label = p.Label,
            Group = Group,
            Priority = 200,
            AllowsEmptySelection = p.AllowsEmptySelection,
            SlashKeyword = p.SlashKeyword,
        }),
    ];

    public async Task<InlineActionResult> ExecuteAsync(string actionId, InlineActionRequest request, CancellationToken cancellationToken)
    {
        if (actionId.StartsWith(CustomPrefix, StringComparison.Ordinal))
            return await ExecuteCustomAsync(actionId, request, cancellationToken).ConfigureAwait(false);

        var writesFromCaret = actionId is "ai.continue" or "ai.beat";

        // Every other action transforms a passage, so it still needs one.
        if (!writesFromCaret && string.IsNullOrWhiteSpace(request.SelectedText))
            return new InlineActionResult { Error = _loc.T("inline.noSelection") };

        var (sys, disposition) = BuildSystem(actionId);
        if (sys == null)
            return new InlineActionResult { Error = $"Unknown action: {actionId}" };

        string user;
        if (writesFromCaret)
        {
            // The prose before the caret is what is being continued. Without it
            // the model has nothing at all to go on, so this is the one input
            // these two actions genuinely require.
            var preceding = string.IsNullOrWhiteSpace(request.PrecedingText)
                ? request.SelectedText
                : request.PrecedingText;
            if (string.IsNullOrWhiteSpace(preceding))
                return new InlineActionResult { Error = _loc.T("inline.nothingToContinue") };

            user = $"STORY SO FAR:\n{preceding}";
            if (actionId == "ai.beat")
            {
                if (string.IsNullOrWhiteSpace(request.Directive))
                    return new InlineActionResult { Error = _loc.T("inline.beatNeedsDirective") };
                user += $"\n\nBEAT TO WRITE:\n{request.Directive}";
            }
        }
        else
        {
            user = $"TEXT:\n{request.SelectedText}";
        }

        // Brainstorm actions enrich the user message with preceding-scene context
        // and a character roster so continuations stay consistent with what came
        // before.
        if (actionId is "ai.brainstorm" or "ai.brainstorm3" or "ai.continue" or "ai.beat")
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

            // An action that asked for several versions hands them over as
            // candidates so the host can show them and the writer can choose.
            // Returning them as one numbered blob and pasting that into the
            // manuscript, which is what happened before, made the writer delete
            // two thirds of what arrived.
            var alternatives = WantsSeveral(actionId) ? SplitNumbered(text) : [];
            if (alternatives.Count > 1) text = alternatives[0];

            return new InlineActionResult
            {
                Text = text,
                Disposition = disposition,
                Alternatives = alternatives,
                // Anything that replaces prose the writer already wrote is a
                // proposal until they have read it. Anything that only adds
                // text is not replacing anything, so it lands as it always did.
                AsSuggestion = disposition == InlineActionDisposition.ReplaceSelection,
            };
        }
        catch (Exception ex)
        {
            return new InlineActionResult { Error = ex.Message };
        }
    }

    /// <summary>
    /// Runs one of the writer's own prompts.
    ///
    /// The template decides what the model is told, so this gathers only the
    /// placeholders that template actually uses - a prompt that does not ask for
    /// the cast should not cost a Codex read.
    /// </summary>
    private async Task<InlineActionResult> ExecuteCustomAsync(
        string actionId, InlineActionRequest request, CancellationToken cancellationToken)
    {
        var id = actionId[CustomPrefix.Length..];
        var prompt = _prompts().FirstOrDefault(p => p.Id == id);
        if (prompt == null)
            return new InlineActionResult { Error = _loc.T("prompts.gone") };

        if (!prompt.AllowsEmptySelection && string.IsNullOrWhiteSpace(request.SelectedText))
            return new InlineActionResult { Error = _loc.T("inline.noSelection") };

        var template = string.IsNullOrWhiteSpace(prompt.Template)
            ? "{{selection}}"
            : prompt.Template;
        var inputs = await GatherAsync(
            PromptLibrary.Uses(template), request, cancellationToken).ConfigureAwait(false);

        var system = prompt.System;
        if (prompt.Candidates > 1)
            system += $"\n\nGive exactly {prompt.Candidates} versions, numbered 1. to "
                + $"{prompt.Candidates}., each genuinely different from the others. "
                + "No preamble, no commentary.";

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = system },
            new() { Role = "user", Content = PromptLibrary.Fill(template, inputs) },
        };

        try
        {
            var result = await _ai.GenerateChatAsync(
                messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            var text = StripCodeFences((result.Response ?? string.Empty).Trim());
            if (string.IsNullOrEmpty(text))
                return new InlineActionResult { Error = _loc.T("inline.emptyResult") };

            var alternatives = prompt.Candidates > 1 ? SplitNumbered(text) : [];
            if (alternatives.Count > 1) text = alternatives[0];

            var disposition = prompt.Disposition switch
            {
                "replace" => InlineActionDisposition.ReplaceSelection,
                "caret" => InlineActionDisposition.InsertAtCaret,
                _ => InlineActionDisposition.InsertAfterSelection,
            };

            return new InlineActionResult
            {
                Text = text,
                Disposition = disposition,
                Alternatives = alternatives,
                AsSuggestion = disposition == InlineActionDisposition.ReplaceSelection,
            };
        }
        catch (Exception ex)
        {
            return new InlineActionResult { Error = ex.Message };
        }
    }

    /// <summary>Fetches only what the template asked for.</summary>
    private async Task<PromptInputs> GatherAsync(
        IReadOnlyList<string> needed, InlineActionRequest request, CancellationToken cancellationToken)
    {
        var scene = string.Empty;
        var chapter = string.Empty;
        var synopsis = string.Empty;
        var pov = string.Empty;
        var characters = string.Empty;
        var context = string.Empty;

        var wantsScene = needed.Any(n => n is "scene" or "chapter" or "synopsis" or "pov");
        if (wantsScene && !string.IsNullOrEmpty(request.ChapterGuid))
        {
            var detail = _host.StoryService.GetSceneDetail(request.ChapterGuid, request.SceneId);
            if (detail != null)
            {
                scene = detail.Title;
                synopsis = detail.Synopsis;
                pov = detail.Pov;
            }
            chapter = _host.ProjectService.GetChaptersOrdered()
                .FirstOrDefault(c => c.Guid == request.ChapterGuid)?.Title ?? string.Empty;
        }

        if (needed.Contains("characters"))
        {
            var builder = new System.Text.StringBuilder();
            foreach (var person in await _host.EntityService.LoadCharactersAsync().ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(person.DisplayName)) continue;
                builder.Append("- ").Append(person.DisplayName);
                if (!string.IsNullOrWhiteSpace(person.Role))
                    builder.Append(" - ").Append(person.Role);
                builder.AppendLine();
            }
            characters = builder.ToString();
        }

        if (needed.Contains("context")
            && !string.IsNullOrEmpty(request.ChapterGuid)
            && !string.IsNullOrEmpty(request.SceneId))
        {
            // Through GetAiContextAsync, so the writer's own inclusion and
            // withholding settings decide what a custom prompt may send. A
            // prompt the writer wrote does not get more access than one we did.
            var entries = await _host.EntityService
                .GetAiContextAsync(request.ChapterGuid, request.SceneId).ConfigureAwait(false);
            var builder = new System.Text.StringBuilder();
            foreach (var entry in entries)
            {
                builder.Append(entry.Name).AppendLine(":");
                foreach (var section in entry.Sections)
                    builder.Append("  ").Append(section.Title).Append(": ")
                        .AppendLine(section.Content);
            }
            context = builder.ToString();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PromptInputs(
            request.SelectedText ?? string.Empty,
            request.PrecedingText ?? string.Empty,
            request.Directive ?? string.Empty,
            scene, chapter, synopsis, pov, characters, context);
    }

    private static bool WantsSeveral(string actionId)
        => actionId is "ai.rewrite3" or "ai.brainstorm3";

    /// <summary>
    /// Splits "1. ... 2. ... 3. ..." into its options.
    ///
    /// A model asked for three numbered versions usually gives three numbered
    /// versions, and sometimes gives two, or four, or one with a preamble. So
    /// this takes what is there rather than insisting on three, and returns
    /// nothing when it cannot find a numbered list at all - in which case the
    /// whole answer is used as a single result, which is the old behaviour and
    /// is never worse than showing an empty picker.
    /// </summary>
    internal static IReadOnlyList<string> SplitNumbered(string text)
    {
        var options = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{1,2})[.)]\s*(.*)$");
            if (match.Success)
            {
                if (current.Length > 0) options.Add(current.ToString().Trim());
                current.Clear();
                current.Append(match.Groups[2].Value);
                continue;
            }

            // A line that is not numbered belongs to the option above it - a
            // version that ran onto a second line is still one version.
            if (current.Length > 0 && line.Length > 0) current.Append(' ').Append(line);
        }
        if (current.Length > 0) options.Add(current.ToString().Trim());

        var cleaned = options.Where(o => o.Length > 0).ToList();
        return cleaned.Count > 1 ? cleaned : [];
    }

    private (string? system, InlineActionDisposition disposition) BuildSystem(string actionId)
    {
        var (system, disposition) = BaseSystem(actionId);
        // Every one of these writes prose that has to sound like the rest of the
        // book, so the voice goes on all of them rather than a chosen few.
        return (system == null ? null : StyleProfiles?.Apply(system) ?? system, disposition);
    }

    private (string? system, InlineActionDisposition disposition) BaseSystem(string actionId)
    {
        var lang = _ai.LanguageName;
        return actionId switch
        {
            // Three wordings rather than one. A rewrite is wrong a fair
            // amount of the time, and the writer picking from three costs
            // nothing next to reading one and undoing it.
            "ai.rewrite3" => ($"You rewrite a passage from a novel three different ways. Preserve meaning, point of view, and tense in every version. Make them genuinely different from each other - different rhythm, emphasis, or word choice - not three near-copies. Output exactly three numbered versions as plain text:\n1. ...\n2. ...\n3. ...\nNo preamble, no commentary. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.rewrite" => ($"You rewrite a passage from a novel. Preserve meaning, point of view, and tense. Improve clarity, rhythm, and word choice. Match the existing voice. Output only the rewritten text — no preamble, no quotes, no commentary. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.expand" => ($"You expand a passage from a novel with additional sensory detail, atmosphere, and beat-level character motion. Stay in the same point of view, tense, and voice. Do not invent plot facts that contradict the passage. Output only the expanded text. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.shorten" => ($"You tighten a passage from a novel. Cut filler, redundancy, and weak modifiers. Preserve meaning, point of view, and tense. Output only the shortened text. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.describe" => ($"You turn the user's input — typically a brief noun phrase or scene seed — into a vivid prose description suitable for a novel. Stay in the surrounding tense and point of view if discernible. Output only the description. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.showdonttell" => ($"You convert telling into showing. The user has given a passage that states emotions, traits, or events flatly. Rewrite it to dramatize those facts through concrete sensory detail, action, and dialogue, without naming the underlying emotion or trait. Preserve point of view and tense. Output only the rewritten passage. Respond in {lang}.", InlineActionDisposition.ReplaceSelection),
            "ai.brainstorm" => ($"You write a single short continuation (1–3 sentences) that could follow the user's passage in the next beat of a novel. Match the voice, point of view, and tense. Use the surrounding scene context and character roster to stay consistent. Do not summarize. Output only the continuation text. Respond in {lang}.", InlineActionDisposition.InsertAfterSelection),
            "ai.brainstorm3" => ($"You propose three distinct continuations that could follow the user's passage in the next beat of a novel. Each option should be 1–3 sentences, divergent from the others (different action, tone, or stakes), and consistent with the voice, point of view, tense, scene context, and character roster supplied. Output exactly three numbered options as plain text:\n1. ...\n2. ...\n3. ...\nNo preamble, no commentary. Respond in {lang}.", InlineActionDisposition.InsertAfterSelection),
            "ai.continue" => ($"You continue a novel from where its author stopped. Write the next 80-150 words of prose, picking up mid-flow from the final sentence you are given - do not restate it, do not summarize, do not open with a transition phrase. Match the voice, point of view, and tense exactly. Use the scene context and character roster to stay consistent. Output only the continuation. Respond in {lang}.", InlineActionDisposition.InsertAtCaret),
            "ai.beat" => ($"You write one beat of a novel towards a specified event. The author gives you the story so far and a short directive describing what should happen next. Dramatize that beat in 80-200 words of prose - do not narrate it in summary, and do not go past it into the following beat. Match the voice, point of view, and tense of what came before. Output only the prose. Respond in {lang}.", InlineActionDisposition.InsertAtCaret),
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

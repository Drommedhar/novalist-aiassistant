using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>What a critique pass did.</summary>
public sealed record CritiqueReport(int Comments, int Suggestions, string? Error);

/// <summary>
/// Reads a scene and leaves its notes where an editor would: in the margin,
/// against the sentence they are about.
///
/// The old shape for this was a panel of findings. A panel is easy to build and
/// easy to ignore, because acting on it means holding a remark in your head,
/// finding the sentence, and deciding. A comment on the sentence is the same
/// information at the point of use, and that is the whole difference.
///
/// Two rules make it safe. A remark becomes a comment, which changes nothing. A
/// proposed wording becomes a suggested edit, which the writer takes or turns
/// down. Nothing here rewrites a sentence, and the SDK would not allow it to.
/// </summary>
public sealed class CritiqueService
{
    private readonly AiService _ai;
    private readonly IHostServices _host;
    private readonly IExtensionLocalization _loc;

    public CritiqueService(AiService ai, IHostServices host, IExtensionLocalization loc)
    {
        _ai = ai;
        _host = host;
        _loc = loc;
    }

    /// <summary>Who the notes are from. Shown on every comment and suggestion.</summary>
    private const string Author = "AI Assistant";

    /// <summary>
    /// Critiques one scene.
    /// </summary>
    /// <param name="proposeEdits">
    /// Whether to also propose wordings. Off by default: a writer who wants to be
    /// told what is wrong has not necessarily asked to be told what to write
    /// instead, and those are different invitations.
    /// </param>
    public async Task<CritiqueReport> CritiqueSceneAsync(
        string chapterGuid,
        string sceneId,
        bool proposeEdits = false,
        CancellationToken cancellationToken = default)
    {
        var html = await _host.ProjectService.ReadSceneContentAsync(chapterGuid, sceneId)
            .ConfigureAwait(false);
        var prose = Prose(html);
        if (prose.Length < 120)
            return new CritiqueReport(0, 0, _loc.T("critique.tooShort"));

        var context = await ContextAsync(chapterGuid, sceneId, cancellationToken).ConfigureAwait(false);

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = SystemPrompt(proposeEdits) },
            new() { Role = "user", Content = context + "SCENE:\n" + prose },
        };

        List<Finding> findings;
        try
        {
            var result = await _ai.GenerateChatAsync(
                messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            findings = Parse(result.Response ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new CritiqueReport(0, 0, null);
        }
        catch (Exception ex)
        {
            return new CritiqueReport(0, 0, ex.Message);
        }

        if (findings.Count == 0)
            return new CritiqueReport(0, 0, _loc.T("critique.nothingFound"));

        var comments = 0;
        var suggestions = 0;

        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The anchor has to be text that is actually in the scene. A model
            // that paraphrases the passage it is talking about would otherwise
            // produce a comment attached to words nobody wrote.
            var anchor = FindAnchor(prose, finding.Anchor);

            await _host.ReviewService.AddCommentAsync(
                chapterGuid, sceneId, anchor ?? string.Empty,
                Note(finding), Author).ConfigureAwait(false);
            comments++;

            if (!proposeEdits || anchor == null || string.IsNullOrWhiteSpace(finding.Rewrite))
                continue;

            // A proposal, not a rewrite. It sits in the prose marked as a
            // suggestion until the writer answers it.
            if (await _host.ReviewService.SuggestEditAsync(
                    chapterGuid, sceneId, anchor, finding.Rewrite!.Trim(), Author)
                .ConfigureAwait(false))
                suggestions++;
        }

        return new CritiqueReport(comments, suggestions, null);
    }

    /// <summary>
    /// Critiques every scene in the book that has enough prose to critique.
    /// Reports as it goes, because this makes one model call per scene and a
    /// writer needs to be able to stop it.
    /// </summary>
    public async Task<CritiqueReport> CritiqueBookAsync(
        bool proposeEdits,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var comments = 0;
        var suggestions = 0;

        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"{chapter.Title} / {scene.Title}");

                var report = await CritiqueSceneAsync(
                    chapter.Guid, scene.Id, proposeEdits, cancellationToken).ConfigureAwait(false);
                comments += report.Comments;
                suggestions += report.Suggestions;
            }
        }

        return new CritiqueReport(comments, suggestions, null);
    }

    /// <summary>One remark, as the model was asked to phrase it.</summary>
    internal sealed record Finding(string Anchor, string Kind, string Note, string? Rewrite);

    private string SystemPrompt(bool proposeEdits)
    {
        var builder = new StringBuilder();
        builder.Append(
            "You are a developmental editor reading one scene of a novel. Find the specific, "
            + "actionable problems - not general praise, not a summary. Look for: a line that "
            + "tells where it should show, dialogue that does not sound like the character, a "
            + "beat that repeats one already made, a sentence whose meaning is unclear, a "
            + "detail that contradicts the context given, pacing that stalls, and filter words "
            + "that hold the reader at arm's length.\n\n");

        builder.Append(
            "Reply as a JSON array and nothing else. Each element:\n"
            + "{\"anchor\": \"an exact phrase copied from the scene, 3-12 words\", "
            + "\"kind\": \"one of: telling, voice, repetition, clarity, continuity, pacing, filter\", "
            + "\"note\": \"one or two sentences saying what is wrong and why\"");

        if (proposeEdits)
            builder.Append(", \"rewrite\": \"a replacement for the anchor phrase, or omit it "
                + "when the fix is not a wording change\"");

        builder.Append("}\n\n");

        builder.Append(
            "The anchor must be copied character for character from the scene. Do not "
            + "paraphrase it and do not quote a passage you have altered - a note attached to "
            + "words the author did not write is worse than no note.\n\n");

        builder.Append(
            "At most six findings, ordered by how much they matter. If the scene is genuinely "
            + $"fine, reply with an empty array. Write the notes in {_ai.LanguageName}.");

        return builder.ToString();
    }

    /// <summary>
    /// What the model should know about the scene beyond its words: who is in the
    /// book, and what happened just before.
    /// </summary>
    private async Task<string> ContextAsync(
        string chapterGuid, string sceneId, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        // The writer's own AI inclusion settings decide what may be sent. Reading
        // the raw Codex here would break a promise the app made on their behalf.
        try
        {
            var entries = await _host.EntityService
                .GetAiContextAsync(chapterGuid, sceneId).ConfigureAwait(false);
            if (entries.Count > 0)
            {
                builder.AppendLine("WHO AND WHAT THIS SCENE INVOLVES:");
                foreach (var entry in entries.Take(12))
                {
                    builder.Append("- ").Append(entry.Name);
                    var first = entry.Sections.FirstOrDefault();
                    if (first != null && !string.IsNullOrWhiteSpace(first.Content))
                        builder.Append(": ").Append(Trim(first.Content, 200));
                    builder.AppendLine();
                }
                builder.AppendLine();
            }
        }
        catch (Exception)
        {
            // Context is an improvement, not a requirement. A critique without it
            // is still worth having.
        }

        var detail = _host.StoryService.GetSceneDetail(chapterGuid, sceneId);
        if (detail != null)
        {
            if (!string.IsNullOrWhiteSpace(detail.Pov))
                builder.AppendLine($"POINT OF VIEW: {detail.Pov}");
            if (!string.IsNullOrWhiteSpace(detail.Synopsis))
                builder.AppendLine($"WHAT THIS SCENE IS FOR: {detail.Synopsis}");
            if (builder.Length > 0) builder.AppendLine();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    /// <summary>
    /// The comment text. The kind goes in front so a writer scanning an inbox of
    /// them can tell a continuity problem from a stylistic one without reading
    /// each in full.
    /// </summary>
    private static string Note(Finding finding)
        => string.IsNullOrWhiteSpace(finding.Kind)
            ? finding.Note
            : $"[{finding.Kind}] {finding.Note}";

    /// <summary>
    /// Locates the anchor in the scene, exactly or nearly.
    ///
    /// Exact first. Failing that, whitespace is normalised on both sides, because
    /// a model copying a phrase across a line break is the commonest near-miss and
    /// refusing it would throw away a good note. Anything looser than that is
    /// refused: guessing which sentence a paraphrase meant would put the remark
    /// on the wrong words.
    /// </summary>
    internal static string? FindAnchor(string prose, string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return null;

        var trimmed = anchor.Trim();
        if (prose.Contains(trimmed, StringComparison.Ordinal)) return trimmed;

        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;

        // Rebuild the phrase as it appears in the prose, with whatever whitespace
        // the prose actually uses between the words.
        var pattern = string.Join(@"\s+", words.Select(System.Text.RegularExpressions.Regex.Escape));
        var match = System.Text.RegularExpressions.Regex.Match(prose, pattern);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Reads the findings out of the model's answer.
    ///
    /// Models wrap JSON in prose and in code fences however firmly they are asked
    /// not to, so the array is located rather than assumed. An answer that cannot
    /// be read at all yields no findings, which reports as "nothing found" - the
    /// alternative is an exception in the editor over a model's formatting.
    /// </summary>
    internal static List<Finding> Parse(string response)
    {
        var json = Isolate(response);
        if (json == null) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var findings = new List<Finding>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var anchor = Text(element, "anchor");
                var note = Text(element, "note");
                if (string.IsNullOrWhiteSpace(note)) continue;

                findings.Add(new Finding(
                    anchor ?? string.Empty,
                    Text(element, "kind") ?? string.Empty,
                    note!,
                    Text(element, "rewrite")));
            }
            return findings;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The outermost JSON array in a response, or null.</summary>
    private static string? Isolate(string response)
    {
        var start = response.IndexOf('[');
        var end = response.LastIndexOf(']');
        return start >= 0 && end > start ? response[start..(end + 1)] : null;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Prose(string html)
    {
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(
            html ?? string.Empty, @"</p\s*>|<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            withBreaks, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(stripped).Trim();
    }

    private static string Trim(string text, int limit)
    {
        var plain = Prose(text);
        return plain.Length <= limit ? plain : plain[..limit] + "...";
    }
}

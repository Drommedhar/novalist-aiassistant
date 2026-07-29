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

/// <summary>Something found in the prose that the Codex does not have.</summary>
public sealed record BibleProposal(
    string TypeKey,
    string Name,
    IReadOnlyList<string> Aliases,
    string Summary,
    IReadOnlyList<(string Title, string Content)> Sections,
    IReadOnlyList<string> FoundIn);

/// <summary>What a bootstrap pass produced.</summary>
public sealed record BibleReport(
    IReadOnlyList<BibleProposal> Proposals, int ScenesRead, string? Error);

/// <summary>
/// Reads a finished manuscript and works out the story bible somebody should have
/// been keeping while they wrote it.
///
/// This is for the writer who has 90,000 words and an empty Codex, which is a very
/// common way to arrive at this application. Doing it by hand means re-reading the
/// whole book with a notebook.
///
/// It proposes and does not create. Every entry is shown with what it found and
/// where, and the writer approves them - which matters more here than anywhere
/// else in this extension, because a bad pass over a whole novel would otherwise
/// fill the Codex with a hundred entries somebody then has to delete one at a
/// time. Approval is also where the two-thirds of proposals that are the same
/// character under different names get merged, and no model does that reliably.
/// </summary>
public sealed class StoryBibleService
{
    private readonly AiService _ai;
    private readonly IHostServices _host;
    private readonly IExtensionLocalization _loc;

    public StoryBibleService(AiService ai, IHostServices host, IExtensionLocalization loc)
    {
        _ai = ai;
        _host = host;
        _loc = loc;
    }

    /// <summary>
    /// How many scenes go into one request. Batched because a request per scene
    /// over a long book is slow and expensive, and because a model that sees
    /// several scenes at once recognises the same person across them - which is
    /// the hard part of this job.
    /// </summary>
    private const int ScenesPerBatch = 6;

    /// <summary>
    /// Reads the book and proposes entries for what the Codex is missing.
    /// </summary>
    public async Task<BibleReport> ProposeAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_host.ProjectService.IsProjectLoaded)
            return new BibleReport([], 0, _loc.T("bible.noProject"));

        var known = await KnownNamesAsync().ConfigureAwait(false);
        var scenes = await ReadScenesAsync(cancellationToken).ConfigureAwait(false);
        if (scenes.Count == 0)
            return new BibleReport([], 0, _loc.T("bible.noProse"));

        var found = new Dictionary<string, BibleProposal>(StringComparer.OrdinalIgnoreCase);

        for (var start = 0; start < scenes.Count; start += ScenesPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = scenes.Skip(start).Take(ScenesPerBatch).ToList();
            progress?.Report($"{Math.Min(start + batch.Count, scenes.Count)} / {scenes.Count}");

            List<BibleProposal> proposals;
            try
            {
                proposals = await AskAsync(batch, known, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One failed batch should not lose the work already done over the
                // rest of the book.
                return new BibleReport([.. found.Values], start, ex.Message);
            }

            foreach (var proposal in proposals) Merge(found, proposal);
        }

        return new BibleReport(
            [.. found.Values
                .OrderBy(p => Order(p.TypeKey))
                .ThenByDescending(p => p.FoundIn.Count)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)],
            scenes.Count, null);
    }

    /// <summary>
    /// Creates the entries the writer approved.
    ///
    /// Two calls per entry, by design: one to create it and one to fill it in.
    /// The alternative was a create-with-everything call, and this way an entry
    /// that fails halfway exists with a name rather than not existing at all.
    /// </summary>
    public async Task<int> CreateAsync(
        IEnumerable<BibleProposal> approved, CancellationToken cancellationToken = default)
    {
        var created = 0;
        foreach (var proposal in approved)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = await _host.EntityService
                .CreateEntityAsync(proposal.TypeKey, proposal.Name, proposal.Summary)
                .ConfigureAwait(false);
            if (id == null) continue;
            created++;

            var sections = new List<CustomEntitySectionInfo>(
                proposal.Sections.Select(s => new CustomEntitySectionInfo
                {
                    Title = s.Title,
                    Content = s.Content
                }));

            // Where it was found goes in as a section. A writer checking one of
            // these needs to be able to get to the scene it came from, and the
            // alternative is taking the summary on trust.
            if (proposal.FoundIn.Count > 0)
                sections.Add(new CustomEntitySectionInfo
                {
                    Title = "Appears in",
                    Content = string.Join(", ", proposal.FoundIn)
                });

            if (sections.Count > 0)
                await _host.EntityService
                    .SaveEntityAsync(proposal.TypeKey, id, sections: sections)
                    .ConfigureAwait(false);
        }
        return created;
    }

    // ── Asking ──

    private async Task<List<BibleProposal>> AskAsync(
        List<(string Chapter, string Scene, string Prose)> batch,
        IReadOnlyCollection<string> known,
        CancellationToken cancellationToken)
    {
        var user = new StringBuilder();

        if (known.Count > 0)
        {
            // The known list is the most valuable thing in the prompt: without it
            // every pass re-proposes the whole cast.
            user.AppendLine("ALREADY IN THE CODEX - do not propose these again:");
            user.AppendLine(string.Join(", ", known.Take(400)));
            user.AppendLine();
        }

        foreach (var (chapter, scene, prose) in batch)
        {
            user.AppendLine($"--- {chapter} / {scene} ---");
            user.AppendLine(prose.Length > 6000 ? prose[..6000] : prose);
            user.AppendLine();
        }

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = SystemPrompt() },
            new() { Role = "user", Content = user.ToString() },
        };

        var result = await _ai.GenerateChatAsync(
            messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(result.Response ?? string.Empty, batch);
    }

    private string SystemPrompt() =>
        "You are building a story bible from a novel's own prose. Read the scenes and list the "
        + "people, places, things and pieces of lore that a reader would need an entry for.\n\n"
        + "Reply as a JSON array and nothing else. Each element:\n"
        + "{\"type\": \"one of: character, location, item, lore\", "
        + "\"name\": \"the fullest form of the name used in the prose\", "
        + "\"aliases\": [\"other names or titles the prose uses for the same thing\"], "
        + "\"summary\": \"one or two sentences, drawn only from what the prose says\", "
        + "\"sections\": [{\"title\": \"short heading\", \"content\": \"what the prose establishes\"}]}\n\n"
        + "Rules that matter more than completeness:\n"
        + "- Only what the prose actually establishes. Do not infer a backstory, a motive or a "
        + "relationship that is not on the page. An entry that invents facts is worse than no entry, "
        + "because the author will believe it later.\n"
        + "- One entry per thing, not per name. If a person is called three things, that is one "
        + "entry with two aliases.\n"
        + "- Skip anything mentioned once in passing with nothing said about it. A city named in a "
        + "list of cities does not need an entry.\n"
        + "- Skip anything already in the Codex.\n\n"
        + $"Write the summaries and sections in {_ai.LanguageName}.";

    internal static List<BibleProposal> Parse(
        string response, List<(string Chapter, string Scene, string Prose)> batch)
    {
        var start = response.IndexOf('[');
        var end = response.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        var where = batch.Select(b => $"{b.Chapter} / {b.Scene}").ToList();

        try
        {
            using var document = JsonDocument.Parse(response[start..(end + 1)]);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var proposals = new List<BibleProposal>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var name = Text(element, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                proposals.Add(new BibleProposal(
                    Kind(Text(element, "type")),
                    name!,
                    Strings(element, "aliases"),
                    Text(element, "summary")?.Trim() ?? string.Empty,
                    Sections(element),
                    where));
            }
            return proposals;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Folds a proposal into what has been found so far.
    ///
    /// The same character turns up in most batches, and the interesting part is
    /// that each pass sees different scenes - so the scene list grows, the aliases
    /// accumulate, and the longest summary wins on the theory that it saw more.
    /// </summary>
    private static void Merge(Dictionary<string, BibleProposal> found, BibleProposal proposal)
    {
        if (!found.TryGetValue(proposal.Name, out var existing))
        {
            found[proposal.Name] = proposal;
            return;
        }

        found[proposal.Name] = existing with
        {
            Aliases = [.. existing.Aliases.Concat(proposal.Aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)],
            Summary = proposal.Summary.Length > existing.Summary.Length
                ? proposal.Summary
                : existing.Summary,
            Sections = [.. existing.Sections.Concat(proposal.Sections)
                .GroupBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.Content.Length).First())],
            FoundIn = [.. existing.FoundIn.Concat(proposal.FoundIn).Distinct(StringComparer.Ordinal)]
        };
    }

    // ── Reading the project ──

    private async Task<HashSet<string>> KnownNamesAsync()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var character in await _host.EntityService.LoadCharactersAsync().ConfigureAwait(false))
        {
            known.Add(character.DisplayName);
            foreach (var alias in character.Aliases) known.Add(alias);
        }
        foreach (var location in await _host.EntityService.LoadLocationsAsync().ConfigureAwait(false))
            known.Add(location.Name);
        foreach (var item in await _host.EntityService.LoadItemsAsync().ConfigureAwait(false))
            known.Add(item.Name);
        foreach (var lore in await _host.EntityService.LoadLoreAsync().ConfigureAwait(false))
            known.Add(lore.Name);

        known.Remove(string.Empty);
        return known;
    }

    private async Task<List<(string Chapter, string Scene, string Prose)>> ReadScenesAsync(
        CancellationToken cancellationToken)
    {
        var scenes = new List<(string, string, string)>();
        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var html = await _host.ProjectService
                    .ReadSceneContentAsync(chapter.Guid, scene.Id).ConfigureAwait(false);
                var prose = Prose(html);
                // A scene with a paragraph in it establishes nothing worth an
                // entry, and including it spends a request on noise.
                if (prose.Length < 200) continue;
                scenes.Add((chapter.Title, scene.Title, prose));
            }
        }
        return scenes;
    }

    private static string Kind(string? type) => type?.ToLowerInvariant() switch
    {
        "location" or "place" => "location",
        "item" or "object" or "thing" => "item",
        "lore" or "concept" or "faction" or "event" => "lore",
        _ => "character"
    };

    private static int Order(string typeKey) => typeKey switch
    {
        "character" => 0,
        "location" => 1,
        "item" => 2,
        _ => 3
    };

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))]
            : [];

    private static IReadOnlyList<(string Title, string Content)> Sections(JsonElement element)
    {
        if (!element.TryGetProperty("sections", out var value)
            || value.ValueKind != JsonValueKind.Array) return [];

        var sections = new List<(string, string)>();
        foreach (var section in value.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object) continue;
            var title = Text(section, "title")?.Trim();
            var content = Text(section, "content")?.Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) continue;
            sections.Add((title!, content!));
        }
        return sections;
    }

    private static string Prose(string html)
    {
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(
            html ?? string.Empty, @"</p\s*>|<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            withBreaks, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(stripped).Trim();
    }
}

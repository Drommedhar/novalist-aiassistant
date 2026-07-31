using System.Text;
using System.Text.Json;
using Novalist.Sdk;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>One derived description of how somebody writes.</summary>
public sealed class StyleProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What the writer calls it. Their book's name, usually.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The description itself, as it will be handed to the model. Prose rather
    /// than fields: what makes a voice is how the parts sit together, and a
    /// form with a "sentence length" box invites an average nobody writes in.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>How many words of prose it was read from.</summary>
    public int SampledWords { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The stored set, and which one is in use.</summary>
public sealed class StyleProfileStore
{
    public List<StyleProfile> Profiles { get; set; } = [];

    /// <summary>Empty for "write in whatever voice the passage suggests".</summary>
    public string ActiveId { get; set; } = string.Empty;
}

/// <summary>
/// A description of the writer's own voice, derived from their own prose, and
/// applied to everything the assistant writes.
///
/// Every generation prompt said "match the existing voice", which asks the model
/// to infer a style from the paragraph it was handed. That works for a rewrite
/// of a distinctive passage and fails everywhere it matters: continuing from a
/// line of dialogue, describing a room, writing towards a beat. What comes back
/// is competent house style, and the writer edits their own voice back into it
/// every time.
///
/// Read once from a spread of real scenes, the voice becomes something the model
/// is told rather than asked to guess.
/// </summary>
public sealed class StyleProfileService
{
    private const int TargetSampleWords = 1800;

    private readonly AiService _ai;
    private readonly IHostServices _host;
    private readonly IExtensionLocalization _loc;
    private readonly string _path;

    private StyleProfileStore _store = new();

    public StyleProfileService(AiService ai, IHostServices host, IExtensionLocalization loc, string path)
    {
        _ai = ai;
        _host = host;
        _loc = loc;
        _path = path;
        Load();
    }

    public IReadOnlyList<StyleProfile> Profiles => _store.Profiles;

    /// <summary>The profile in use, or null when the writer has chosen none.</summary>
    public StyleProfile? Active
        => _store.Profiles.FirstOrDefault(p => p.Id == _store.ActiveId);

    /// <summary>
    /// The system prompt with the writer's voice appended, or unchanged when
    /// there is no active profile. Last, deliberately: it is the instruction the
    /// rest is in service of, and a model that drops the tail of a long prompt
    /// should drop the boilerplate rather than this.
    /// </summary>
    public string Apply(string systemPrompt)
    {
        var active = Active;
        if (active == null || string.IsNullOrWhiteSpace(active.Description)) return systemPrompt;

        return systemPrompt
            + "\n\nWrite in this author's voice. This description was derived from their own "
            + "prose and outranks any general sense of good style - where the two disagree, "
            + "follow the author.\n\n"
            + active.Description.Trim();
    }

    public void SetActive(string? profileId)
    {
        _store.ActiveId = profileId ?? string.Empty;
        Save();
    }

    public void Remove(string profileId)
    {
        _store.Profiles.RemoveAll(p => p.Id == profileId);
        if (_store.ActiveId == profileId) _store.ActiveId = string.Empty;
        Save();
    }

    /// <summary>
    /// Reads a spread of the writer's prose and asks for a description of it.
    /// Returns null with a notification when there is not enough to read or the
    /// model did not answer.
    /// </summary>
    public async Task<StyleProfile?> BuildAsync(
        string name,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (sample, words) = await CollectSampleAsync(progress, cancellationToken).ConfigureAwait(false);
        if (words < 400)
        {
            _host.ShowNotification(_loc.T("style.tooLittle"));
            return null;
        }

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = SystemPrompt() },
            new() { Role = "user", Content = sample },
        };

        string? answer;
        try
        {
            var result = await _ai.GenerateChatAsync(
                messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            answer = result.Response;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(answer))
        {
            _host.ShowNotification(_loc.T("style.failed"));
            return null;
        }

        var profile = new StyleProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? _loc.T("style.defaultName") : name.Trim(),
            Description = answer.Trim(),
            SampledWords = words,
        };
        _store.Profiles.Add(profile);
        // A profile nobody selected is one that changes nothing, and a writer
        // who just built one meant to use it.
        _store.ActiveId = profile.Id;
        Save();
        return profile;
    }

    private string SystemPrompt() =>
        "You are describing how one author writes, from a sample of their prose, so that "
        + "another writer could imitate it. Write the description as instructions addressed to "
        + "that writer.\n\n"
        + "Cover what is actually distinctive here: sentence length and how it varies, where "
        + "the rhythm falls, how dialogue is punctuated and attributed, how much interiority "
        + "there is and how it is marked, what the descriptions attend to and what they skip, "
        + "the level of diction, recurring constructions, and how paragraphs open and close.\n\n"
        + "Be concrete and quote short phrases from the sample as evidence. Do not praise the "
        + "writing, do not suggest improvements, and do not describe the plot or the "
        + "characters - a description of what happens is useless for writing something else. "
        + "If a trait is ordinary, leave it out; a list of things every novel does is not a "
        + "voice. Around 200-300 words, as prose, no headings.\n\n"
        + $"Write the description in {_ai.LanguageName}.";

    /// <summary>
    /// Prose from across the book rather than the front of it. An opening is the
    /// most rewritten thing a writer owns and the least like the rest of them;
    /// sampling only there describes a voice that appears on one page.
    /// </summary>
    private async Task<(string Sample, int Words)> CollectSampleAsync(
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var ids = new List<(string Chapter, string Scene)>();
        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
                ids.Add((chapter.Guid, scene.Id));
        }
        if (ids.Count == 0) return (string.Empty, 0);

        // Evenly spaced, so a run of dialogue-heavy chapters in one part of the
        // book does not become the whole answer.
        var builder = new StringBuilder();
        var words = 0;
        var step = Math.Max(1, ids.Count / 8);

        // Two passes over the same list: the spread first, then whatever is left
        // if a short book did not fill the target. Sampling the front of the
        // book instead would describe the one part of it that has been rewritten
        // twenty times.
        foreach (var pass in new[] { true, false })
        {
            for (var i = 0; i < ids.Count && words < TargetSampleWords; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i % step == 0 != pass) continue;

                var html = await _host.ProjectService
                    .ReadSceneContentAsync(ids[i].Chapter, ids[i].Scene).ConfigureAwait(false);
                var excerpt = FirstWords(Prose(html), 400);
                if (excerpt.Words == 0) continue;

                builder.AppendLine(excerpt.Text).AppendLine();
                words += excerpt.Words;
                progress?.Report($"{words}/{TargetSampleWords}");
            }
            if (step == 1) break;
        }

        return (builder.ToString(), words);
    }

    /// <summary>Tags out, paragraph breaks kept.</summary>
    internal static string Prose(string html)
    {
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(
            html ?? string.Empty, @"</p\s*>|<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            withBreaks, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(stripped).Trim();
    }

    internal static (string Text, int Words) FirstWords(string text, int limit)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (string.Empty, 0);
        var taken = Math.Min(limit, parts.Length);
        return (string.Join(" ", parts.Take(taken)), taken);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _store = JsonSerializer.Deserialize<StyleProfileStore>(File.ReadAllText(_path))
                ?? new StyleProfileStore();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A profile store that will not load is not a reason to refuse to
            // write: the writer loses the voice, not the assistant.
            _store = new StyleProfileStore();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowNotification(_loc.T("style.saveFailed"));
        }
    }
}

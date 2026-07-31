namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// One way of reading a scene, and what that reader notices.
/// </summary>
/// <param name="Key">
/// Stable and unlocalised: it is stored on a request and shown on a comment, so
/// it has to mean the same thing in every language.
/// </param>
/// <param name="Brief">
/// What this reader is for, written as an instruction to the model. Kept here
/// rather than in the locale files because it is a prompt, not interface text -
/// translating it would change what the model is asked to do.
/// </param>
/// <param name="Kinds">
/// The finding kinds this lens is allowed to raise. Without them every lens
/// reports the same seven things and a "line editor" hands back notes about
/// pacing, which is the whole reason lenses are worth having.
/// </param>
public sealed record CritiqueLens(
    string Key,
    string Brief,
    IReadOnlyList<string> Kinds);

/// <summary>
/// Who is reading the scene.
///
/// One developmental editor found seven kinds of problem at once and every
/// scene came back with the same shape of note: a bit of telling, a bit of
/// pacing, a filter word. Useful once, then noise, because a writer polishing
/// sentences was still being told the middle sags and a writer checking
/// structure was being told about adverbs.
///
/// A named reader with a brief gives notes worth reading twice: the same scene
/// through the line editor and through the continuity reader says two different
/// things, and both are actionable in a way the average of them is not.
/// </summary>
public static class CritiqueLenses
{
    /// <summary>The one the app used before lenses existed.</summary>
    public const string DevelopmentalKey = "developmental";

    public static IReadOnlyList<CritiqueLens> All { get; } =
    [
        new(DevelopmentalKey,
            "You are a developmental editor. You care about whether the scene works: whether "
            + "somebody wants something, whether anything is in the way, whether the situation "
            + "is different at the end. Ignore sentence-level polish - somebody else is doing "
            + "that pass, and a note about an adverb is not what this read is for.",
            ["structure", "stakes", "pacing", "motivation"]),

        new("line",
            "You are a line editor. You care about the sentences: a line that tells where it "
            + "should show, a filter word holding the reader at arm's length, a rhythm that "
            + "stumbles, a word doing no work. Say nothing about plot or structure - that is a "
            + "different pass and the writer did not ask for it here.",
            ["telling", "filter", "rhythm", "wordiness"]),

        new("voice",
            "You are reading only for voice. Does each character sound like themselves and "
            + "unlike each other? Does the narration sound like this point-of-view character "
            + "rather than like the author? Flag dialogue that any of them could have said and "
            + "narration that would read identically from somebody else's head.",
            ["voice", "dialogue", "narration"]),

        new("continuity",
            "You are a continuity reader. You care only about whether this scene contradicts "
            + "what you have been told: a fact that does not match the context, a character "
            + "knowing something they have not learned, a detail that changes between "
            + "sentences, time that does not add up. Say nothing about how well it is written.",
            ["continuity", "knowledge", "time"]),

        new("firstReader",
            "You are an ordinary reader, not an editor. Say where you were confused, where you "
            + "were bored, where you stopped believing it, and where you wanted to keep going. "
            + "Speak plainly and in the first person - 'I lost track of who was speaking here' - "
            + "and do not offer craft terminology or fixes.",
            ["confusing", "boring", "unconvincing", "working"]),

        new("agent",
            "You are an agent reading the opening of a submission. You are looking for the "
            + "reasons you would stop reading: a slow start, a voice that is competent but not "
            + "distinctive, a familiar premise handled familiarly, a first page that could "
            + "belong to twenty other books. Be specific about where you would have stopped.",
            ["hook", "distinctiveness", "familiarity", "momentum"]),
    ];

    /// <summary>A lens by key, falling back to the developmental read.</summary>
    public static CritiqueLens Resolve(string? key)
        => All.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? All[0];
}

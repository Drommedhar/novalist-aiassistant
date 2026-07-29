using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>Why a piece of context was included, which decides where it goes.</summary>
public enum ContextTier
{
    /// <summary>The instruction. Never dropped; without it nothing else means anything.</summary>
    Instruction = 0,

    /// <summary>The passage being worked on. Dropping this makes the request meaningless.</summary>
    Subject = 1,

    /// <summary>Who and what the scene involves.</summary>
    Entities = 2,

    /// <summary>What this scene is for, and where it sits.</summary>
    SceneNotes = 3,

    /// <summary>What happened just before.</summary>
    Preceding = 4,

    /// <summary>Everything else worth having if there is room.</summary>
    Background = 5
}

/// <summary>One block of context, with what it is worth.</summary>
public sealed record ContextBlock(ContextTier Tier, string Heading, string Body, int Weight = 0);

/// <summary>What was assembled and what had to go.</summary>
public sealed record AssembledContext(
    string Text, int EstimatedTokens, IReadOnlyList<string> Dropped);

/// <summary>
/// Decides what a model is told, in what order, and what gets cut when there is
/// not room.
///
/// Every AI feature here had its own answer to this, assembled inline: a roster
/// here, two preceding scenes there, a synopsis if it happened to be handy. That
/// meant the same question got three different answers, and no answer at all to
/// the one that matters - what to drop when the context does not fit.
///
/// The order is not stylistic. Models attend most reliably to the start and the
/// end of a long context, so the instruction leads and the passage being worked on
/// comes last, with the background in the middle where inattention costs least.
/// And what gets cut is decided by tier rather than by position, so running out of
/// room loses the least important thing rather than whatever happened to be at the
/// bottom.
/// </summary>
public sealed class ContextEngine
{
    /// <summary>
    /// Characters per token, roughly, for English prose.
    ///
    /// Four is the usual rule of thumb and it is wrong for any particular string.
    /// It is used here to decide what to drop, not to promise a count - which is
    /// why the budget is applied with room to spare rather than to the last token.
    /// </summary>
    private const double CharactersPerToken = 4.0;

    /// <summary>
    /// How much of the budget to actually use. The estimate is approximate, and
    /// the failure mode of getting it wrong is a refused request, so the safety
    /// margin is worth more than the last few hundred tokens.
    /// </summary>
    private const double Headroom = 0.85;

    public int TokenBudget { get; set; } = 8000;

    /// <summary>
    /// Assembles the blocks into one context, dropping from the least important
    /// tier down until it fits.
    /// </summary>
    public AssembledContext Assemble(IEnumerable<ContextBlock> blocks)
    {
        // Sorted by what a block is worth, and within a tier by the weight the
        // caller gave it - a character the scene actually mentions outranks one
        // pinned into every scene.
        var ordered = blocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Body))
            .OrderBy(b => (int)b.Tier)
            .ThenByDescending(b => b.Weight)
            .ToList();

        var limit = (int)(Math.Max(500, TokenBudget) * Headroom * CharactersPerToken);
        var dropped = new List<string>();

        // Instruction and Subject are never dropped: a request without them is not
        // a smaller request, it is a different and useless one. If those two alone
        // exceed the budget the caller has a problem this cannot solve, and
        // truncating the passage silently would be the worst way to find out.
        var required = ordered.Where(b => b.Tier <= ContextTier.Subject).ToList();
        var optional = ordered.Where(b => b.Tier > ContextTier.Subject).ToList();

        var used = required.Sum(b => Size(b));
        var kept = new List<ContextBlock>(required);

        foreach (var block in optional)
        {
            var size = Size(block);
            if (used + size > limit)
            {
                dropped.Add(block.Heading);
                continue;
            }
            kept.Add(block);
            used += size;
        }

        // Written out in reading order rather than importance order: the
        // instruction first, the passage last, background in the middle.
        var text = new StringBuilder();
        foreach (var block in kept.OrderBy(b => Position(b.Tier)))
        {
            if (!string.IsNullOrWhiteSpace(block.Heading))
                text.Append(block.Heading.ToUpperInvariant()).Append(":\n");
            text.Append(block.Body.Trim()).Append("\n\n");
        }

        var assembled = text.ToString().TrimEnd();
        return new AssembledContext(
            assembled, (int)Math.Ceiling(assembled.Length / CharactersPerToken), dropped);
    }

    /// <summary>
    /// Where a tier is written, as opposed to how readily it is dropped.
    ///
    /// The subject goes last because that is where a model reads most reliably,
    /// even though it is the thing least willing to be cut.
    /// </summary>
    private static int Position(ContextTier tier) => tier switch
    {
        ContextTier.Instruction => 0,
        ContextTier.Entities => 1,
        ContextTier.Background => 2,
        ContextTier.SceneNotes => 3,
        ContextTier.Preceding => 4,
        ContextTier.Subject => 5,
        _ => 3
    };

    private static int Size(ContextBlock block)
        => block.Heading.Length + block.Body.Length + 4;

    /// <summary>
    /// Whether an entry is worth including for this scene at all.
    ///
    /// The rule is the writer's, not ours: an entry they marked as always-include
    /// goes in, one the scene mentions goes in, and one they excluded never does.
    /// This exists so the decision is written down once instead of being
    /// re-implemented per feature.
    /// </summary>
    public static bool ShouldInclude(string inclusion, bool mentionedInScene)
        => inclusion?.ToLowerInvariant() switch
        {
            "always" => true,
            "never" => false,
            _ => mentionedInScene
        };

    /// <summary>
    /// How much an entry is worth relative to others in its tier. Something the
    /// scene names is more relevant than something pinned into every scene, and
    /// when there is not room for both, that is the one to keep.
    /// </summary>
    public static int Relevance(bool mentionedInScene, bool confirmedMention)
        => confirmedMention ? 100 : mentionedInScene ? 50 : 10;

    public string Serialise() => JsonSerializer.Serialize(
        new Stored(TokenBudget), new JsonSerializerOptions { WriteIndented = true });

    public static ContextEngine Load(string? json)
    {
        var engine = new ContextEngine();
        if (string.IsNullOrWhiteSpace(json)) return engine;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (stored is { TokenBudget: > 0 })
                engine.TokenBudget = Math.Clamp(stored.TokenBudget, 500, 200000);
        }
        catch (JsonException)
        {
            return new ContextEngine();
        }
        return engine;
    }

    private sealed record Stored(int TokenBudget);
}

using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

internal sealed record ChatRequestPlan(
    List<AiChatMessage> Messages, ContextPreview Preview, int OutputTokenReserve, IReadOnlyList<string> Dropped);

internal static class ChatRequestAssembler
{
    public static ChatRequestPlan Build(string system, IReadOnlyList<AiChatMessage> history,
        string text, IReadOnlyList<ContextBlock> context, int tokenBudget)
    {
        var reserve = Math.Clamp(tokenBudget / 4, 128, 2048);
        var inputBudget = Math.Max(1, (int)(tokenBudget * 0.85) - reserve);
        context = context.Where(block => !string.IsNullOrWhiteSpace(block.Body)).ToList();
        var required = context.Where(block => block.Tier <= ContextTier.Subject).ToList();
        var optional = context.Where(block => block.Tier > ContextTier.Subject)
            .OrderBy(block => block.Tier).ThenByDescending(block => block.Weight).ToList();
        var kept = new List<ContextBlock>(required);
        var turns = new List<AiChatMessage>();
        var dropped = new List<string>();
        var messages = Compose(system, turns, text, kept);
        if (Estimate(messages) > inputBudget)
            throw new InvalidOperationException("The message and selected passage exceed the context budget. Shorten them or increase the context budget.");

        foreach (var block in optional)
        {
            kept.Add(block);
            if (Estimate(Compose(system, turns, text, kept)) <= inputBudget) continue;
            kept.RemoveAt(kept.Count - 1);
            dropped.Add(block.Heading);
        }

        var start = history.Count;
        while (start > 0)
        {
            var previous = start - 1;
            while (previous > 0 && history[previous].Role != "user") previous--;
            turns.InsertRange(0, history.Skip(previous).Take(start - previous));
            if (Estimate(Compose(system, turns, text, kept)) > inputBudget)
            {
                turns.RemoveRange(0, start - previous);
                dropped.Add($"Earlier conversation ({start} messages)");
                break;
            }
            start = previous;
        }

        messages = Compose(system, turns, text, kept);
        var lines = ContextEngine.Describe(context, kept);
        lines.Insert(0, new ContextLine("Instruction", "System", system.Length, Tokens(system), true));
        lines.Add(new ContextLine("Preceding", "Conversation", turns.Sum(turn => turn.Content.Length), Estimate(turns), true));
        lines.Add(new ContextLine("Subject", "Current message", text.Length, Tokens(text), true));
        var preview = new ContextPreview(string.Join("\n\n", messages.Select(message => $"[{message.Role}]\n{message.Content}")),
            Estimate(messages), inputBudget, lines);
        return new ChatRequestPlan(messages, preview, reserve, dropped);
    }

    private static List<AiChatMessage> Compose(string system, List<AiChatMessage> turns, string text, List<ContextBlock> context)
    {
        var assembled = new ContextEngine { TokenBudget = int.MaxValue / 8 }.Assemble(context).Text;
        return [new() { Role = "system", Content = system }, .. turns,
            new() { Role = "user", Content = assembled.Length == 0 ? text : assembled + "\n\n" + text }];
    }

    private static int Tokens(string text) => (int)Math.Ceiling(text.Length / 4.0);
    private static int Estimate(IEnumerable<AiChatMessage> messages)
        => 3 + messages.Sum(message => Tokens(message.Content) + 8);
}

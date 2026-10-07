using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed class ChatRequestAssemblerTests
{
    [Fact]
    public void FullRequestFitsBudgetAndKeepsRecentCompleteTurns()
    {
        var history = Enumerable.Range(0, 100).SelectMany(n => new[]
        {
            new AiChatMessage { Role = "user", Content = $"Question {n}: " + new string('q', 200) },
            new AiChatMessage { Role = "assistant", Content = $"Answer {n}: " + new string('a', 200) }
        }).ToList();
        var plan = ChatRequestAssembler.Build("System prompt", history, "Current question",
            [new(ContextTier.Entities, "Selected", "Context marker")], 2000);
        Assert.True(plan.Preview.EstimatedTokens <= plan.Preview.TokenBudget);
        Assert.True(plan.Preview.EstimatedTokens + plan.OutputTokenReserve < 2000);
        Assert.Contains(plan.Dropped, dropped => dropped.StartsWith("Earlier conversation"));
        Assert.Equal("user", plan.Messages[1].Role);
        Assert.Contains("Answer 99", plan.Messages[^2].Content);
        Assert.Contains("Context marker", plan.Messages[^1].Content);
        Assert.DoesNotContain(plan.Messages.Take(plan.Messages.Count - 1), message => message.Content.Contains("Context marker"));
        Assert.Equal(string.Join("\n\n", plan.Messages.Select(message => $"[{message.Role}]\n{message.Content}")), plan.Preview.Text);
    }

    [Fact]
    public void OversizedRequiredTextIsRejectedBeforeSending()
    {
        Assert.Throws<InvalidOperationException>(() => ChatRequestAssembler.Build("System", [], new string('x', 5000), [], 500));
        Assert.Throws<InvalidOperationException>(() => ChatRequestAssembler.Build("System", [], "Question",
            [new(ContextTier.Subject, "Selected scene", new string('x', 5000))], 500));
    }

    [Fact]
    public void LargeOptionalContextIsDroppedAndReportedByIdentity()
    {
        var plan = ChatRequestAssembler.Build("System", [], "Question", [
            new(ContextTier.Entities, "Same name", "keep"),
            new(ContextTier.Background, "Same name", new string('x', 5000))], 500);
        Assert.Single(plan.Dropped);
        var matching = plan.Preview.Lines.Where(line => line.Heading == "Same name").ToArray();
        Assert.True(matching[0].Kept);
        Assert.False(matching[1].Kept);
    }

    [Fact]
    public void RepeatedBlockInstanceReportsEachOccurrenceIndependently()
    {
        var context = new ContextBlock(ContextTier.Background, "Repeated", new string('x', 700));
        var plan = ChatRequestAssembler.Build("System", [], "Question", [context, context], 500);
        var lines = plan.Preview.Lines.Where(line => line.Heading == "Repeated").ToArray();
        Assert.True(lines[0].Kept);
        Assert.False(lines[1].Kept);
        Assert.Single(plan.Dropped);
        Assert.Equal(700, plan.Messages[^1].Content.Count(character => character == 'x'));

        context = context with { Body = new string('x', 1000) };
        var preview = new ContextEngine { TokenBudget = 500 }.Preview([context, context]);
        Assert.True(preview.Lines[0].Kept);
        Assert.False(preview.Lines[1].Kept);
        Assert.Equal(1000, preview.Text.Count(character => character == 'x'));
    }

    [Fact]
    public void BlankBlocksDoNotAppearAsIncludedContext()
    {
        var plan = ChatRequestAssembler.Build("System", [], "Question",
            [new(ContextTier.Subject, "Blank", " \n ")], 500);
        Assert.DoesNotContain(plan.Preview.Lines, line => line.Heading == "Blank");
        Assert.DoesNotContain("Blank", plan.Preview.Text);
    }
}

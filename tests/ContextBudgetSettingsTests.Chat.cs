using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed partial class ContextBudgetSettingsTests
{
    [Theory]
    [InlineData("0", 14952)]
    [InlineData("8000", 4800)]
    public async Task ChatBudgetControlsPersistTheSameSettingsAndUpdatePreview(string contextLength, int inputBudget)
    {
        using var controller = new ChatWebViewController(_host, _extension);
        var reply = await controller.OnMessageAsync(JsonSerializer.Serialize(new { type = "setBudgets", contextBudget = "20000", contextLength }));
        Assert.NotNull(reply);
        using var saved = JsonDocument.Parse(reply);
        Assert.Equal("budgets", saved.RootElement.GetProperty("type").GetString());
        Assert.Equal(inputBudget, saved.RootElement.GetProperty("budgets").GetProperty("inputBudget").GetInt32());
        Assert.Equal(20000, ContextEngine.Load(File.ReadAllText(Path.Combine(_directory, "context.json"))).TokenBudget);
        var field = Assert.Single(_extension.GetSettingsSchema().Fields, field => field.Key == "contextBudget");
        Assert.Equal("20000", field.Value);
        var preview = await controller.OnMessageAsync("""{"type":"preview","text":"Hello"}""");
        Assert.NotNull(preview);
        using var document = JsonDocument.Parse(preview);
        Assert.Equal(inputBudget, document.RootElement.GetProperty("tokenBudget").GetInt32());
    }

    [Theory]
    [InlineData("invalid", "0")]
    [InlineData("499", "0")]
    [InlineData("20000", "-1")]
    [InlineData("2000001", "0")]
    public async Task InvalidChatBudgetsAreRejectedWithoutChangingSettings(string contextBudget, string contextLength)
    {
        using var controller = new ChatWebViewController(_host, _extension);
        var reply = await controller.OnMessageAsync(JsonSerializer.Serialize(new { type = "setBudgets", contextBudget, contextLength }));
        Assert.NotNull(reply);
        using var document = JsonDocument.Parse(reply);
        Assert.Equal("budgetsRejected", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(8000, _extension.ContextEngine.TokenBudget);
        Assert.False(File.Exists(Path.Combine(_directory, "context.json")));
    }
}

using System.Text.Json;

namespace Novalist.Extensions.AiAssistant.Services;

public sealed partial class ChatWebViewController
{
    private object BudgetSnapshot() => new
    {
        contextBudget = _extension.ContextEngine.TokenBudget,
        contextLength = _extension.Settings.ContextLength,
        minimum = ContextEngine.MinimumTokenBudget, maximum = ContextEngine.MaximumTokenBudget,
        inputBudget = ChatRequestAssembler.InputBudget(_vm.EffectiveContextBudget),
        outputReserve = ChatRequestAssembler.OutputReserve(_vm.EffectiveContextBudget)
    };

    private async Task<string> SetBudgetsAsync(JsonElement message, int generation)
    {
        var budget = Text(message, "contextBudget");
        var length = Text(message, "contextLength");
        if (!int.TryParse(budget, out var tokens) || tokens < ContextEngine.MinimumTokenBudget || tokens > ContextEngine.MaximumTokenBudget
            || !int.TryParse(length, out var contextLength) || contextLength < 0 || contextLength > 131072)
            throw new ArgumentException(_loc.T("ai.chatBudgetInvalid"));
        RequireCurrent(generation);
        await _extension.ApplySettingsAsync(new Dictionary<string, string>
        {
            ["contextBudget"] = budget, ["contextLength"] = length
        });
        RequireCurrent(generation);
        return JsonSerializer.Serialize(new { type = "budgets", budgets = BudgetSnapshot() }, Json);
    }
}

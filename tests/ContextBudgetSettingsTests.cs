using System.Reflection;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed partial class ContextBudgetSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "novalist-budget-" + Guid.NewGuid());
    private readonly IHostServices _host;
    private readonly AiAssistantExtension _extension = new();

    public ContextBudgetSettingsTests()
    {
        Directory.CreateDirectory(_directory);
        var loc = ServiceProxy.Create<IExtensionLocalization>((method, args) => method.Name switch
        {
            "T" or "get_Item" => (string?)args[0],
            _ => throw new InvalidOperationException(method.Name)
        });
        _host = ServiceProxy.Create<IHostServices>((method, _) => method.Name switch
        {
            "GetExtensionSettingsPath" => _directory,
            "GetLocalization" => loc,
            "WriteHostDataAsync" => Task.CompletedTask,
            "get_CurrentLanguageDisplayName" => "English",
            "GetAiHooks" => Array.Empty<Novalist.Sdk.Hooks.IAiHook>(),
            "add_ProjectLoaded" or "remove_ProjectLoaded" => null,
            _ => throw new InvalidOperationException(method.Name)
        });
        SetField("_host", _host);
        SetField("_loc", loc);
    }

    [Theory]
    [InlineData("128000", 128000)]
    [InlineData("1000000", 1000000)]
    [InlineData("1", 500)]
    [InlineData("3000000", 2000000)]
    public async Task SettingsExposeAndPersistTheBudget(string input, int expected)
    {
        await _extension.ApplySettingsAsync(new Dictionary<string, string> { ["contextBudget"] = input });
        var field = Assert.Single(_extension.GetSettingsSchema().Fields, field => field.Key == "contextBudget");
        Assert.Equal(expected.ToString(), field.Value);
        Assert.Equal(ContextEngine.MaximumTokenBudget, field.Max);
        Assert.Equal(expected, ContextEngine.Load(File.ReadAllText(Path.Combine(_directory, "context.json"))).TokenBudget);
    }

    [Fact]
    public async Task LargerBudgetActuallyKeepsLargeContextAndRespectsConfiguredContextLength()
    {
        using var vm = new AiChatViewModel(_host, _extension);
        ContextBlock[] context = [new(ContextTier.Entities, "Large dossier", new string('x', 80000))];
        Assert.Contains("Large dossier", vm.Preview("Hello", context).Dropped);
        await _extension.ApplySettingsAsync(new Dictionary<string, string> { ["contextBudget"] = "128000" });
        var expanded = vm.Preview("Hello", context);
        Assert.Empty(expanded.Dropped);
        Assert.Equal(106752, expanded.Preview.TokenBudget);
        await _extension.ApplySettingsAsync(new Dictionary<string, string> { ["contextLength"] = "8000" });
        Assert.Contains("Large dossier", vm.Preview("Hello", context).Dropped);
        Assert.Equal(128000, _extension.ContextEngine.TokenBudget);
    }

    private void SetField(string name, object value)
    {
        var field = typeof(AiAssistantExtension).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(_extension, value);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

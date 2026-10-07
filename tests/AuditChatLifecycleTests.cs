using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed class AuditChatLifecycleTests
{
    [Fact]
    public async Task EmptyPickerDoesNotReadOrSendTheProjectRoster()
    {
        using var fixture = new ChatFixture();
        using var vm = new AiChatViewModel(fixture.Host, fixture.Extension) { UserInput = "Hello" };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.DoesNotContain("private-entity", fixture.Requests.Single());
        Assert.Contains("Hello", fixture.Requests.Single());
    }

    [Fact]
    public async Task PickedEntitiesUseOnlyPrivacyFilteredSections()
    {
        using var fixture = new ChatFixture();
        using var controller = new ChatWebViewController(fixture.Host, fixture.Extension);
        var preview = await controller.OnMessageAsync("""{"type":"preview","include":["entity:allowed","entity:private-entity"]}""");
        Assert.Contains("allowed-section", preview);
        Assert.DoesNotContain("private-entity", preview);
        await controller.OnMessageAsync("""{"type":"send","text":"Hello","include":["entity:allowed","entity:private-entity"]}""");
        await fixture.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("allowed-section", fixture.Requests.Single());
        Assert.DoesNotContain("private-entity", fixture.Requests.Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearOrProjectSwitchRejectsLateResponsesAndHistory(bool switchProject)
    {
        using var fixture = new ChatFixture { HoldResponse = true };
        using var vm = new AiChatViewModel(fixture.Host, fixture.Extension) { UserInput = "project-a-marker" };
        var first = vm.SendCommand.ExecuteAsync(null);
        await fixture.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (switchProject) fixture.SwitchProject();
        else await vm.ClearChatCommand.ExecuteAsync(null);
        fixture.ResponseReleased.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(vm.Messages);
        Assert.Empty(vm.StreamingResponse);
        Assert.False(vm.IsGenerating);
        fixture.HoldResponse = false;
        vm.UserInput = "project-b-marker";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.DoesNotContain("project-a-marker", fixture.Requests.Last());
        Assert.Equal("project-b-marker", vm.Messages[0].Content);
    }

    [Theory]
    [InlineData("send", "project")]
    [InlineData("send", "reset")]
    [InlineData("send", "dispose")]
    [InlineData("send", "cancel")]
    [InlineData("preview", "project")]
    [InlineData("preview", "reset")]
    [InlineData("preview", "dispose")]
    [InlineData("preview", "cancel")]
    public async Task LateContextCannotCrossAConversationBoundary(string operation, string boundary)
    {
        using var fixture = new ChatFixture { HoldContext = true };
        using var controller = new ChatWebViewController(fixture.Host, fixture.Extension);
        var updates = new List<string>();
        controller.MessagePosted += updates.Add;
        var pending = controller.OnMessageAsync(JsonSerializer.Serialize(new
        {
            type = operation, text = "old-project-message", include = new[] { "entity:allowed" }
        }));
        await fixture.ContextRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(pending.IsCompleted);
        if (boundary == "project") fixture.SwitchProject();
        else if (boundary == "dispose") controller.Dispose();
        else await controller.OnMessageAsync(JsonSerializer.Serialize(new { type = boundary }));
        fixture.ContextReleased.TrySetResult();
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(fixture.Requests);
        Assert.DoesNotContain(updates, update => update.Contains("allowed-section") || update.Contains("old-project-message"));
        if (boundary == "dispose") return;
        await controller.OnMessageAsync("""{"type":"send","text":"new-project-message","include":[]}""");
        await fixture.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("allowed-section", fixture.Requests.Single());
        Assert.DoesNotContain("old-project-message", fixture.Requests.Single());
    }

    [Fact]
    public async Task SendUsesTheAlreadyValidatedPromptWithoutInvokingHooksAgain()
    {
        using var fixture = new ChatFixture();
        var builds = 0;
        fixture.Hooks = [new ChatHook(() => ++builds == 1 ? "first-hook-prompt" : new string('x', 100000))];
        using var controller = new ChatWebViewController(fixture.Host, fixture.Extension);
        await controller.OnMessageAsync("""{"type":"send","text":" Hello ","include":[]}""");
        await fixture.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, builds);
        Assert.Contains("first-hook-prompt", Assert.Single(fixture.Requests));
    }

    [Fact]
    public async Task LateUiChunksCannotRepopulateCompletedOrSubsequentResponses()
    {
        using var fixture = new ChatFixture { QueueUi = true };
        using var vm = new AiChatViewModel(fixture.Host, fixture.Extension) { UserInput = "first" };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.NotEmpty(fixture.PendingUi);
        Assert.Equal("answer", vm.Messages[^1].Content);
        Assert.Empty(vm.StreamingResponse);
        fixture.HoldResponse = true;
        vm.UserInput = "second";
        var second = vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.IsGenerating);
        fixture.FlushUi();
        Assert.Empty(vm.StreamingResponse);
        fixture.ResponseReleased.TrySetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.FlushUi();
        Assert.Empty(vm.StreamingResponse);
        Assert.Equal(4, vm.Messages.Count);
    }

    [Fact]
    public async Task ResponseFiltersRemainAppliedToFinalMessageAndLaterConversation()
    {
        using var fixture = new ChatFixture { QueueUi = true };
        fixture.Hooks = [new ChatHook(() => null, text => text.Replace("answer", "filtered"))];
        using var vm = new AiChatViewModel(fixture.Host, fixture.Extension) { UserInput = "first" };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("filtered", vm.Messages[^1].Content);
        var preview = vm.Preview("follow-up", []);
        Assert.Contains(preview.Messages, message => message.Role == "assistant" && message.Content == "filtered");
        Assert.DoesNotContain(preview.Messages, message => message.Content == "answer");
        fixture.FlushUi();
        Assert.Empty(vm.StreamingResponse);
    }

    [Fact]
    public async Task ReentrantProjectSwitchDuringPromptHookPreventsSendingOldText()
    {
        using var fixture = new ChatFixture();
        fixture.Hooks = [new ChatHook(() => { fixture.SwitchProject(); return "new-project-context"; })];
        using var vm = new AiChatViewModel(fixture.Host, fixture.Extension) { UserInput = "old-project-text" };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Requests);
        Assert.Empty(vm.Messages);
        Assert.False(vm.IsGenerating);
        Assert.DoesNotContain("old-project-text", vm.Preview("new-message", []).Preview.Text);
    }

    [Theory]
    [InlineData("send", "sendRejected")]
    [InlineData("preview", "previewRejected")]
    public async Task ContextReadFailuresReturnAnActionableReply(string operation, string replyType)
    {
        using var fixture = new ChatFixture { ContextError = new IOException("Context could not be read") };
        using var controller = new ChatWebViewController(fixture.Host, fixture.Extension);
        var reply = await controller.OnMessageAsync(JsonSerializer.Serialize(new
        {
            type = operation, text = "draft", include = new[] { "entity:allowed" }
        }));
        Assert.NotNull(reply);
        using var document = JsonDocument.Parse(reply);
        Assert.Equal(replyType, document.RootElement.GetProperty("type").GetString());
        Assert.Equal("draft", document.RootElement.GetProperty("text").GetString());
        Assert.Equal("Context could not be read", document.RootElement.GetProperty("error").GetString());
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task SendingWhileAResponseIsActiveReturnsTheUnsentDraft()
    {
        using var fixture = new ChatFixture { HoldResponse = true };
        using var controller = new ChatWebViewController(fixture.Host, fixture.Extension);
        await controller.OnMessageAsync("""{"type":"send","text":"first","include":[]}""");
        await fixture.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var reply = await controller.OnMessageAsync("""{"type":"send","text":"second draft","include":[]}""");
        Assert.NotNull(reply);
        using var document = JsonDocument.Parse(reply);
        Assert.Equal("sendRejected", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("second draft", document.RootElement.GetProperty("text").GetString());
        Assert.Single(fixture.Requests);
        fixture.ResponseReleased.TrySetResult();
    }

    private sealed class ChatHook(Func<string?> build, Func<string, string>? filter = null) : IAiHook
    {
        public string? OnBuildSystemPrompt(Novalist.Sdk.Models.AiPromptContext context) => build();
        public string OnResponseChunk(string chunk) => filter?.Invoke(chunk) ?? chunk;
    }

    private sealed class ChatFixture : IDisposable
    {
        private readonly HttpClient _http;
        private Action<ProjectInfo>? _projectLoaded;
        public List<string> Requests { get; } = [];
        public TaskCompletionSource RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResponseReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContextRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContextReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldResponse { get; set; }
        public bool HoldContext { get; set; }
        public bool QueueUi { get; set; }
        public Exception? ContextError { get; set; }
        public Queue<Action> PendingUi { get; } = new();
        public IReadOnlyList<IAiHook> Hooks { get; set; } = [];
        public IHostServices Host { get; }
        public AiAssistantExtension Extension { get; }

        public ChatFixture()
        {
            _http = new HttpClient(new AuditAiRequestTests.Handler(async (request, _) =>
            {
                Requests.Add(await request.Content!.ReadAsStringAsync());
                RequestReceived.TrySetResult();
                if (HoldResponse) await ResponseReleased.Task;
                return AuditAiRequestTests.Stream("data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n");
            }));
            Extension = new AiAssistantExtension(AuditAiRequestTests.Compatible(_http));
            var localization = ServiceProxy.Create<IExtensionLocalization>((method, args) => method.Name switch
            {
                "T" or "get_Item" => (string)args[0]!,
                _ => throw new InvalidOperationException(method.Name)
            });
            var project = ServiceProxy.Create<IExtensionProjectService>((method, _) => method.Name switch
            {
                "get_CurrentScene" => new SceneInfo { Id = "scene", ChapterGuid = "chapter" },
                _ => throw new InvalidOperationException(method.Name)
            });
            var entities = ServiceProxy.Create<IExtensionEntityService>((method, _) => method.Name switch
            {
                "GetAiContextAsync" => ReadContextAsync(),
                _ => throw new InvalidOperationException("Chat accessed unfiltered entity data: " + method.Name)
            });
            Host = ServiceProxy.Create<IHostServices>((method, args) =>
            {
                switch (method.Name)
                {
                    case "GetLocalization": return localization;
                    case "get_ProjectService": return project;
                    case "get_EntityService": return entities;
                    case "get_CurrentLanguage": return "en";
                    case "get_CurrentLanguageDisplayName": return "English";
                    case "GetAiHooks": return Hooks;
                    case "PostToUI":
                        var action = (Action)args[0]!;
                        if (QueueUi) PendingUi.Enqueue(action); else action();
                        return null;
                    case "add_ProjectLoaded": _projectLoaded += (Action<ProjectInfo>)args[0]!; return null;
                    case "remove_ProjectLoaded": _projectLoaded -= (Action<ProjectInfo>)args[0]!; return null;
                    default: throw new InvalidOperationException(method.Name);
                }
            });
        }

        public void SwitchProject() => _projectLoaded?.Invoke(new ProjectInfo());
        public void FlushUi()
        {
            while (PendingUi.TryDequeue(out var action)) action();
        }
        private async Task<IReadOnlyList<AiContextEntryInfo>> ReadContextAsync()
        {
            if (ContextError != null) throw ContextError;
            ContextRequested.TrySetResult();
            if (HoldContext) await ContextReleased.Task;
            return [new() { Id = "allowed", Name = "Allowed", Sections = [new() { Title = "Profile", Content = "allowed-section" }] }];
        }
        public void Dispose() => _http.Dispose();
    }
}

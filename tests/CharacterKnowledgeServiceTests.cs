using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.Models;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed class CharacterKnowledgeServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyingRecordCompletesForColdAndWarmCache(bool warm)
    {
        var fixture = new KnowledgeFixture();
        if (warm) await fixture.Service.LoadAsync("same-character");
        await fixture.Apply("scene-a").WaitAsync(TimeSpan.FromSeconds(3));
        var saved = fixture.Saved("scene-a");
        Assert.Single(saved.Scenes);
        Assert.Equal("same-character", saved.CharacterId);
    }

    [Fact]
    public async Task ForkedProjectsWithSameCharacterIdHaveSeparateCaches()
    {
        var fixture = new KnowledgeFixture();
        await fixture.Apply("only-a");
        fixture.Root = Path.Combine(Path.GetTempPath(), "knowledge-project-b");
        Assert.Empty((await fixture.Service.LoadAsync("same-character")).Scenes);
        await fixture.Apply("only-b");
        Assert.Equal("only-b", Assert.Single(fixture.Saved("only-b").Scenes).SceneId);
        fixture.Root = Path.Combine(Path.GetTempPath(), "knowledge-project-a");
        Assert.Equal("only-a", Assert.Single((await fixture.Service.LoadAsync("same-character")).Scenes).SceneId);
    }

    [Fact]
    public async Task ConcurrentSceneResultsAreBothPersisted()
    {
        var fixture = new KnowledgeFixture();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(n => fixture.Apply($"scene-{n}")))
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(20, fixture.Saved("scene-19").Scenes.Count);
    }

    [Fact]
    public async Task CancellationWhileWaitingForFileLockCompletesWithoutSecondWrite()
    {
        var fixture = new KnowledgeFixture();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeExists = async () => { reading.SetResult(); await release.Task; };
        var first = fixture.Apply("first");
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var waiting = fixture.Apply("cancelled", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("first", Assert.Single(fixture.Saved("first").Scenes).SceneId);
    }

    [Fact]
    public async Task PendingWriteKeepsItsOriginalProjectPath()
    {
        var fixture = new KnowledgeFixture();
        var originalRoot = fixture.Root;
        fixture.BeforeExists = () =>
        {
            fixture.Root = Path.Combine(Path.GetTempPath(), "knowledge-project-b");
            return Task.CompletedTask;
        };
        await fixture.Apply("original");
        Assert.All(fixture.Files.Keys, path => Assert.StartsWith(originalRoot, path));
        Assert.Empty((await fixture.Service.LoadAsync("same-character")).Scenes);
    }

    [Fact]
    public async Task FailedSaveDoesNotPublishAnUnsavedCacheEntry()
    {
        var fixture = new KnowledgeFixture();
        await fixture.Apply("saved");
        fixture.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Apply("not-saved"));
        Assert.Equal("saved", Assert.Single((await fixture.Service.LoadAsync("same-character")).Scenes).SceneId);
    }

    [Fact]
    public async Task CorruptKnowledgeIsNotOverwrittenWithAnEmptyFile()
    {
        var fixture = new KnowledgeFixture();
        var path = Path.Combine(fixture.Root, "knowledge", "same-character.json");
        fixture.Files[path] = "not JSON";
        await Assert.ThrowsAsync<JsonException>(() => fixture.Apply("new"));
        Assert.Equal("not JSON", fixture.Files[path]);
    }

    private sealed class KnowledgeFixture
    {
        public string Root { get; set; } = Path.Combine(Path.GetTempPath(), "knowledge-project-a");
        public ConcurrentDictionary<string, string> Files { get; } = new();
        public Func<Task>? BeforeExists { get; set; }
        public bool FailWrites { get; set; }
        public CharacterKnowledgeService Service { get; }

        public KnowledgeFixture()
        {
            var files = ServiceProxy.Create<IExtensionFileService>((method, args) => method.Name switch
            {
                "CombinePath" => Path.Combine((string[])args[0]!),
                "ExistsAsync" => Exists((string)args[0]!),
                "ReadTextAsync" => Task.FromResult(Files[(string)args[0]!]),
                "WriteTextAsync" => Write((string)args[0]!, (string)args[1]!),
                "CreateDirectoryAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException(method.Name)
            });
            var host = ServiceProxy.Create<IHostServices>((method, _) => method.Name switch
            {
                "get_FileService" => files,
                "GetExtensionDataPath" => Root,
                _ => throw new InvalidOperationException(method.Name)
            });
            Service = new CharacterKnowledgeService(host, new KnowledgeBuilder(new AiService(), host), "test");
        }

        public Task Apply(string sceneId, CancellationToken cancellationToken = default)
            => Service.ApplyRecordAsync(new SceneAnalysisRecord { SceneId = sceneId },
                [new CharacterInfo { Id = "same-character", DisplayName = "Character" }], "Scene text", cancellationToken);

        public CharacterKnowledgeFile Saved(string sceneId)
            => Files.Values.Select(json => JsonSerializer.Deserialize<CharacterKnowledgeFile>(json)!)
                .Single(file => file.Scenes.Any(scene => scene.SceneId == sceneId));

        private async Task<bool> Exists(string path)
        {
            if (BeforeExists is not null) await BeforeExists();
            return Files.ContainsKey(path);
        }

        private Task Write(string path, string text)
        {
            if (FailWrites) throw new IOException("Synthetic write failure.");
            Files[path] = text;
            return Task.CompletedTask;
        }
    }
}

public class ServiceProxy : DispatchProxy
{
    private Func<MethodInfo, object?[], object?> _handler = null!;

    public static T Create<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy)._handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => _handler(targetMethod!, args ?? []);
}

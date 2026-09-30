using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Xunit;

public class DictationTests
{
    [Fact]
    public async Task WarmupLoadsTheChosenModelsWithoutAudioOrChatConfiguration()
    {
        using var cancellation = new CancellationTokenSource();
        using var runtime = new FakeRuntime(async (request, token) =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            Assert.Equal("warmup", json.GetProperty("operation").GetString());
            Assert.Single(json.EnumerateObject());
            await Task.Delay(Timeout.Infinite, token);
            return "";
        }) { SpeechModel = "large-v3", Acceleration = "rocm" };
        var service = new DictationService(runtime);
        var pending = service.WarmUpAsync(new AiSettings { Enabled = false, DictationModel = "large-v3",
            DictationAcceleration = "rocm" }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WarmUpAsync(new AiSettings { DictationEnabled = false }, default));
    }
    [Theory]
    [InlineData("Hello, she said.", """{"segments":[{"text":"Hello,","kind":"dialogue","newParagraph":true},{"text":"she said.","kind":"attribution"}]}""")]
    [InlineData("Komm zurück sagte sie", """{"segments":[{"text":"Komm zurück!","kind":"dialogue","newParagraph":true},{"text":"sagte sie.","kind":"attribution"}]}""")]
    [InlineData("Don't leave.", """{"segments":[{"text":"Don’t leave.","kind":"dialogue","newParagraph":true}]}""")]
    public void PreservesEnglishGermanAndApostrophes(string transcript, string response)
        => Assert.NotEmpty(DictationService.ParseSegments(transcript, response));

    [Theory]
    [InlineData("""{"segments":[{"text":"Hello, darling.","kind":"dialogue"}]}""")]
    [InlineData("""{"segments":[{"text":"Hello hello.","kind":"dialogue"}]}""")]
    [InlineData("""{"segments":[{"text":"Goodbye.","kind":"dialogue"}]}""")]
    [InlineData("""{"segments":[{"text":"Hello.","kind":"instructions"}]}""")]
    [InlineData("""{"segments":[]}""")]
    public void RejectsRewritingRepetitionAndInvalidOutput(string response)
        => Assert.Throws<InvalidOperationException>(() => DictationService.ParseSegments("Hello.", response));

    [Fact]
    public void AcceptsFencedJsonWithoutTreatingTheFenceAsProse()
        => Assert.Single(DictationService.ParseSegments("Hallo.", "```json\n{\"segments\":[{\"text\":\"Hallo.\",\"kind\":\"dialogue\"}]}\n```"));

    [Theory]
    [InlineData("en", "small", "cpu")]
    [InlineData("de", "small", "auto")]
    [InlineData("en", "large-v3", "cuda")]
    [InlineData("de", "large-v3", "rocm")]
    [InlineData("de", "small", "mlx")]
    public async Task PassesAudioOnlyToLocalRuntimeWithoutChatSettings(string language, string speechModel, string acceleration)
    {
        using var runtime = new FakeRuntime((request, _) =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            Assert.Equal(language, json.GetProperty("language").GetString());
            Assert.Equal("transcribe", json.GetProperty("operation").GetString());
            Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(json.GetProperty("audio").GetString()!));
            Assert.DoesNotContain("secret", json.ToString());
            Assert.DoesNotContain("http", json.ToString());
            return Task.FromResult("Hallo Welt.");
        }) { SpeechModel = speechModel, Acceleration = acceleration };
        LocalDictationRuntime.ValidateModels(speechModel, "4B");
        var settings = new AiSettings { DictationModel = speechModel, DictationAcceleration = acceleration,
            Enabled = false, Provider = "anthropic", AnthropicApiKey = "secret" };
        var text = await new DictationService(runtime).TranscribeAsync(settings, [1, 2, 3], "audio/wav", language, CancellationToken.None);
        Assert.Equal("Hallo Welt.", text);
    }

    [Fact]
    public async Task DialogueUsesTheSameLocalRuntimeAndValidatesItsOutput()
    {
        using var runtime = new FakeRuntime((request, _) =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            Assert.Equal("format", json.GetProperty("operation").GetString());
            Assert.Equal("Hallo.", json.GetProperty("transcript").GetString());
            Assert.Equal("Die Tür ging auf.", json.GetProperty("precedingText").GetString());
            Assert.False(json.TryGetProperty("audio", out var unused));
            return Task.FromResult("""{"segments":[{"text":"Hallo mein Freund.","kind":"dialogue"}]}""");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DictationService(runtime)
            .DetectDialogueAsync(new AiSettings(), "Hallo.", "de", "Die Tür ging auf.", CancellationToken.None));
    }

    [Fact]
    public async Task InvalidFormattingGetsOneStricterRetryWithoutRewritingTheTranscript()
    {
        var instructions = new List<string>();
        using var runtime = new FakeRuntime((request, _) =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            Assert.Equal("Hallo.", json.GetProperty("transcript").GetString());
            instructions.Add(json.GetProperty("instruction").GetString()!);
            return Task.FromResult(instructions.Count == 1
                ? """{"segments":[{"text":"Hallo sagte sie.","kind":"dialogue"}]}"""
                : """{"segments":[{"text":"Hallo.","kind":"dialogue"}]}""");
        });
        var result = await new DictationService(runtime).DetectDialogueAsync(new AiSettings(), "Hallo.", "de", "", default);
        Assert.Equal("Hallo.", Assert.Single(result).Text);
        Assert.Equal(2, instructions.Count);
        Assert.StartsWith(instructions[0], instructions[1]);
        Assert.Contains("never append", instructions[1]);
    }

    [Fact]
    public async Task CancellationReachesTheSpeechRequest()
    {
        var entered = new TaskCompletionSource();
        using var runtime = new FakeRuntime(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        });
        using var cancellation = new CancellationTokenSource();
        var pending = new DictationService(runtime).TranscribeAsync(new AiSettings(), [1], "audio/wav", "en", cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrUnpreparedDictationCannotStart(bool enabled, bool ready)
    {
        using var runtime = new FakeRuntime((_, _) => throw new Exception("Must not run")) { Ready = ready };
        var service = new DictationService(runtime);
        var settings = new AiSettings { DictationEnabled = enabled };
        Assert.False(service.IsConfigured(settings));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranscribeAsync(settings, [1], "audio/wav", "en", default));
    }

    [Theory]
    [InlineData("http://localhost:8000", "4B")]
    [InlineData("small", "../../model")]
    [InlineData("base.en", "1.7B")]
    public void ModelSelectionCannotBecomeAPathOrExternalService(string speech, string dialogue)
        => Assert.Throws<ArgumentException>(() => LocalDictationRuntime.ValidateModels(speech, dialogue));

    [Fact]
    public async Task UnpreparedRuntimeFailsWithoutInstallingOrStartingAnything()
    {
        var path = Path.Combine(Path.GetTempPath(), "nl-dictation-test-" + Guid.NewGuid());
        using var runtime = new LocalDictationRuntime(path);
        Assert.False(runtime.IsReady("small", "4B"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RequestAsync("small", "4B", new { }, default));
        Assert.False(Directory.Exists(path));
    }

    [Theory]
    [InlineData("265BC0124E3B4C5541510EBD3B9652603B8D891C08AF099CEFC2392F9962D7AC")]
    [InlineData("371BBE9496A758E93B8590C2349D01D46506FD5D6D2916F7FA481D8D83A128C9")]
    public async Task CompatibleWorkerUpgradeKeepsInstalledModelsWithoutRepair(string previousRecipe)
    {
        var root = Path.Combine(Path.GetTempPath(), "nl-dictation-upgrade-" + Guid.NewGuid());
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var venv = Path.Combine(root, "venv" + (OperatingSystem.IsMacOS() ? "-" + architecture : ""));
        var python = Path.Combine(venv, OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, ""); // Intentionally non-executable: never launch Python in this unit test.
        File.WriteAllText(Path.Combine(root, "worker.py"), "old worker");
        var marker = Path.Combine(root, $"ready-small-4B-cpu-{architecture}.txt");
        File.WriteAllText(marker, previousRecipe);
        var weights = Path.Combine(root, "model-fixture.bin");
        File.WriteAllText(weights, "keep installed weights");
        try
        {
            using var runtime = new LocalDictationRuntime(root);
            Assert.True(runtime.IsReady("small", "4B", "cpu"));
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => runtime.RequestAsync("small", "4B",
                new { operation = "warmup" }, default, "cpu"));
            Assert.Contains("warmup", File.ReadAllText(Path.Combine(root, "worker.py")));
            Assert.NotEqual(previousRecipe, File.ReadAllText(marker));
            Assert.Equal("keep installed weights", File.ReadAllText(weights));
            Assert.True(runtime.IsReady("small", "4B", "cpu"));
            File.WriteAllText(marker, "unknown-recipe");
            Assert.False(runtime.IsReady("small", "4B", "cpu"));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeRuntime(Func<object, CancellationToken, Task<string>> run) : IDictationRuntime
    {
        public string SpeechModel { get; init; } = "small";
        public string Acceleration { get; init; } = "auto";
        public bool Ready { get; init; } = true;
        public bool IsReady(string speech, string dialogue, string acceleration = "auto") => Ready;
        public Task<string> RequestAsync(string speech, string dialogue, object request, CancellationToken token, string acceleration = "auto")
        {
            Assert.Equal(SpeechModel, speech);
            Assert.Equal(Acceleration, acceleration);
            Assert.Equal("4B", dialogue);
            return run(request, token);
        }
        public Task PrepareAsync(string speech, string dialogue, Action<string, string> progress, CancellationToken token, string acceleration = "auto")
            => throw new NotImplementedException();
        public void Dispose() { }
    }
}

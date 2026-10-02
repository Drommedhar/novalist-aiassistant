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
            Assert.True(json.GetProperty("automaticDialogue").GetBoolean());
            Assert.Equal(2, json.EnumerateObject().Count());
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
    [Fact]
    public async Task PlainWarmupAndSpellingHintsAreIndependentOfChatAndFormatting()
    {
        using var runtime = new FakeRuntime((request, _) =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            if (json.GetProperty("operation").GetString() == "warmup")
                Assert.False(json.GetProperty("automaticDialogue").GetBoolean());
            else
                Assert.Equal(new[] { "Aeloria", "Großwald" }, json.GetProperty("vocabulary").EnumerateArray().Select(term => term.GetString()));
            Assert.DoesNotContain("private manuscript", json.ToString());
            return Task.FromResult("Aeloria.");
        });
        var service = new DictationService(runtime);
        var settings = new AiSettings { Enabled = false };
        await service.WarmUpAsync(settings, default, false);
        Assert.Equal("Aeloria.", await service.TranscribeAsync(settings, [1], "audio/wav", "en", default, ["Aeloria", "Großwald"]));
        Assert.True(typeof(Novalist.Sdk.Hooks.IDictationOptionsContributor).IsAssignableFrom(typeof(Novalist.Extensions.AiAssistant.AiAssistantExtension)));
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
    [InlineData("Was zum Mama? sagte er.", "Was zum Mama?", "sagte er.", "")]
    [InlineData("Was zum Mama? sagte er. Vielen Dank.", "Was zum Mama?", "sagte er.", "Vielen Dank.")]
    [InlineData("What was that? he said. Thank you.", "What was that?", "he said.", "Thank you.")]
    [InlineData("Wait! she shouted.", "Wait!", "she shouted.", "")]
    [InlineData("Komm herein, flüsterte sie, es ist kalt.", "Komm herein,", "flüsterte sie,", "es ist kalt.")]
    public void SeparatesBareSpeechTagsThatTheModelIncludedInDialogue(string transcript, string speech, string tag, string continuation)
    {
        var response = JsonSerializer.Serialize(new { segments = new[] { new { text = transcript, kind = "dialogue", newParagraph = true } } });
        var segments = DictationService.ParseSegments(transcript, response);
        Assert.Equal(new Novalist.Sdk.Hooks.DictationSegment(speech, "dialogue", true), segments[0]);
        Assert.Equal(new Novalist.Sdk.Hooks.DictationSegment(tag, "attribution"), segments[1]);
        Assert.Equal(continuation.Length == 0 ? 2 : 3, segments.Count);
        if (continuation.Length > 0)
            Assert.Equal(new Novalist.Sdk.Hooks.DictationSegment(continuation, "dialogue"), segments[2]);
        Assert.Equal(transcript, string.Join(" ", segments.Select(s => s.Text)));
    }

    [Theory]
    [InlineData("»Tretet hinein, werte neue Schüler, hörte man eine Stimme laut durch die Hallen rufen«", "Tretet hinein, werte neue Schüler,", "hörte man eine Stimme laut durch die Hallen rufen.", "")]
    [InlineData("Bleibt draußen, liebe Besucher, hörte Mara eine Stimme aus dem Keller flüstern. Hier ist es gefährlich.", "Bleibt draußen, liebe Besucher,", "hörte Mara eine Stimme aus dem Keller flüstern.", "Hier ist es gefährlich.")]
    [InlineData("Wartet hier, hörten sie die Stimme aus dem Gang rufen.", "Wartet hier,", "hörten sie die Stimme aus dem Gang rufen.", "")]
    [InlineData("Komm herein, hörte er eine leise Stimme sagen.", "Komm herein,", "hörte er eine leise Stimme sagen.", "")]
    [InlineData("Come closer, dear travelers, a voice was heard calling from the fog. We can help you.", "Come closer, dear travelers,", "a voice was heard calling from the fog.", "We can help you.")]
    [InlineData("Enter, new students, a voice called from deep within the hall.", "Enter, new students,", "a voice called from deep within the hall.", "")]
    [InlineData("Wartet hier, hörte man eine Stimme rufen. Die Tore bleiben zu.", "Wartet hier,", "hörte man eine Stimme rufen.", "Die Tore bleiben zu.")]
    [InlineData("Stay here, a voice called from the hall. That is an order.", "Stay here,", "a voice called from the hall.", "That is an order.")]
    public void SeparatesHeardVoiceReportingClausesEvenInsideCopiedQuotes(string transcript, string speech, string tag, string continuation)
    {
        var response = JsonSerializer.Serialize(new { segments = new[] { new { text = transcript, kind = "dialogue", newParagraph = true } } });
        var segments = DictationService.ParseSegments(transcript, response);
        Assert.Equal(new Novalist.Sdk.Hooks.DictationSegment(speech, "dialogue", true), segments[0]);
        Assert.Equal(new Novalist.Sdk.Hooks.DictationSegment(tag, "attribution"), segments[1]);
        Assert.Equal(continuation.Length == 0 ? 2 : 3, segments.Count);
        if (continuation.Length > 0)
            Assert.Equal(new Novalist.Sdk.Hooks.DictationSegment(continuation, "dialogue"), segments[2]);
    }

    [Theory]
    [InlineData("\"Hello,\",")]
    [InlineData("“Hello,”,")]
    [InlineData("„Hello,“")]
    [InlineData("»Hello,«,")]
    public void RemovesCopiedOuterDialogueQuotesBeforeCheckingReportingClauses(string text)
    {
        var response = JsonSerializer.Serialize(new { segments = new[] { new { text, kind = "dialogue" } } });
        Assert.Equal("Hello,", Assert.Single(DictationService.ParseSegments("Hello,", response)).Text);
    }

    [Theory]
    [InlineData("Sie sagte, dass der Zug zu spät kommen würde.", "narration")]
    [InlineData("Was zum Mama? sagte er.", "narration")]
    [InlineData("Why? He said nothing about a train.", "dialogue")]
    [InlineData("What did she say?", "dialogue")]
    [InlineData("Liam hörte eine Stimme laut durch die Hallen rufen.", "narration")]
    [InlineData("Man hörte eine Stimme rufen, dass alle gehen sollten.", "narration")]
    [InlineData("Warte, hörte man eine Stimme rufen, dass alle gehen sollten.", "dialogue")]
    [InlineData("Warte, hörte man eine Stimme rufen, die Liam vertraut vorkam.", "dialogue")]
    [InlineData("Yes, a voice called from the hall that she recognized.", "dialogue")]
    [InlineData("Warte, hörte man eine Stimme rufen hören.", "dialogue")]
    public void SpeechTagCorrectionLeavesNarrationAndReportedSpeechAlone(string transcript, string kind)
    {
        var response = JsonSerializer.Serialize(new { segments = new[] { new { text = transcript, kind } } });
        var segment = Assert.Single(DictationService.ParseSegments(transcript, response));
        Assert.Equal(transcript, segment.Text);
        Assert.Equal(kind, segment.Kind);
    }

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
        var attempts = 0;
        using var runtime = new FakeRuntime((request, _) =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            Assert.Equal("format", json.GetProperty("operation").GetString());
            Assert.Equal(attempts++ == 0 ? "Die Tür ging auf. Hallo." : "Hallo.", json.GetProperty("transcript").GetString());
            Assert.Equal("", json.GetProperty("precedingText").GetString());
            Assert.False(json.TryGetProperty("audio", out var unused));
            return Task.FromResult("""{"segments":[{"text":"Hallo mein Freund.","kind":"dialogue"}]}""");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DictationService(runtime)
            .DetectDialogueAsync(new AiSettings(), "Hallo.", "de", "Die Tür ging auf.", CancellationToken.None));
        Assert.Equal(2, attempts);
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
        Assert.All(instructions, instruction => Assert.DoesNotContain("\r", instruction));
    }

    [Fact]
    public async Task InvalidCombinedPassageIsRetriedWithoutEarlierDictationAndStillRequiresEveryTranscriptWord()
    {
        const string transcript = "Was zum Mama? sagte er. Vielen Dank.";
        const string context = "Liam ging die Treppe hinunter.";
        var attempts = 0;
        using var runtime = new FakeRuntime((request, _) =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            Assert.Equal(attempts++ == 0 ? context + " " + transcript : transcript, json.GetProperty("transcript").GetString());
            Assert.Equal("", json.GetProperty("precedingText").GetString());
            return Task.FromResult(attempts == 1
                ? JsonSerializer.Serialize(new { segments = new[] { new { text = context + " " + context + " " + transcript, kind = "narration" } } })
                : """{"segments":[{"text":"Was zum Mama?","kind":"dialogue","newParagraph":true},{"text":"sagte er.","kind":"attribution"},{"text":"Vielen Dank.","kind":"dialogue"}]}""");
        });
        var result = await new DictationService(runtime).DetectDialogueAsync(new AiSettings(), transcript, "de", context, default);
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { "dialogue", "attribution", "dialogue" }, result.Select(s => s.Kind));
        Assert.Equal(transcript, string.Join(" ", result.Select(s => s.Text)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextIsRemovedExactlyEvenWhenItSharesASegmentWithRepeatedNewWords(bool merged)
    {
        var calls = 0;
        using var runtime = new FakeRuntime((request, _) =>
        {
            calls++;
            var json = JsonSerializer.SerializeToElement(request);
            Assert.Equal("Hello. Hello again.", json.GetProperty("transcript").GetString());
            return Task.FromResult(merged
                ? """{"segments":[{"text":"Hello. Hello again.","kind":"dialogue","newParagraph":true}]}"""
                : """{"segments":[{"text":"Hello.","kind":"dialogue","newParagraph":true},{"text":"Hello again.","kind":"dialogue","newParagraph":false}]}""");
        });
        var result = await new DictationService(runtime).DetectDialogueAsync(new AiSettings(), "Hello again.", "en",
            "Earlier unrelated paragraph.\nHello.", default);
        Assert.Equal(new Novalist.Sdk.Hooks.DictationSegment("Hello again.", "dialogue"), Assert.Single(result));
        Assert.Equal(1, calls);
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
    [InlineData("B37107DB793DD7DC4573CEB84A2C78D533650AA91B43B3B868B63CDF8604BF8F")]
    [InlineData("DCD002304D36BB1F0646ECE204238E31C66ADB4AF9CB3BCB14333F7DA79E42E6")]
    [InlineData("1FAC0972CF15740A94C07A7E7E06384B0DE1AAA824BD5E2FC4E06835FB919A35")]
    [InlineData("DCFD3418D493242F6964A1D0FBF56CA84741FD7C634EE4AC17B3047D27091A38")]
    [InlineData("1FAC0972CF15740A94C07A7E7E06384B0DE1AAA824BD5E2FC4E06835FB919A35", "cuda")]
    [InlineData("DCFD3418D493242F6964A1D0FBF56CA84741FD7C634EE4AC17B3047D27091A38", "cuda")]
    [InlineData("1FAC0972CF15740A94C07A7E7E06384B0DE1AAA824BD5E2FC4E06835FB919A35", "rocm")]
    [InlineData("DCFD3418D493242F6964A1D0FBF56CA84741FD7C634EE4AC17B3047D27091A38", "rocm")]
    public async Task CompatibleWorkerUpgradeKeepsInstalledModelsWithoutRepair(string previousRecipe, string backend = "cpu")
    {
        var root = Path.Combine(Path.GetTempPath(), "nl-dictation-upgrade-" + Guid.NewGuid());
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        if (backend != "cpu" && (OperatingSystem.IsMacOS() || architecture != System.Runtime.InteropServices.Architecture.X64)) return;
        var venv = Path.Combine(root, (backend == "cpu" ? "venv" : "venv-" + backend) + (OperatingSystem.IsMacOS() ? "-" + architecture : ""));
        var python = Path.Combine(venv, OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, ""); // Intentionally non-executable: never launch Python in this unit test.
        File.WriteAllText(Path.Combine(root, "worker.py"), "old worker");
        var marker = Path.Combine(root, $"ready-small-4B-{backend}-{architecture}.txt");
        File.WriteAllText(marker, previousRecipe);
        var weights = Path.Combine(root, "model-fixture.bin");
        File.WriteAllText(weights, "keep installed weights");
        try
        {
            using var runtime = new LocalDictationRuntime(root);
            Assert.True(runtime.IsReady("small", "4B", backend));
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => runtime.RequestAsync("small", "4B",
                new { operation = "warmup" }, default, backend));
            Assert.Contains("warmup", File.ReadAllText(Path.Combine(root, "worker.py")));
            Assert.NotEqual(previousRecipe, File.ReadAllText(marker));
            Assert.Equal("keep installed weights", File.ReadAllText(weights));
            Assert.True(runtime.IsReady("small", "4B", backend));
            File.WriteAllText(marker, "unknown-recipe");
            Assert.False(runtime.IsReady("small", "4B", backend));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("cuda")]
    [InlineData("rocm")]
    [InlineData("mlx")]
    public void AcceleratedRuntimeNeedsPreparationForNewSpeechFilterDependencies(string backend)
    {
        var root = Path.Combine(Path.GetTempPath(), "nl-dictation-vad-upgrade-" + Guid.NewGuid());
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var venv = Path.Combine(root, "venv-" + backend + (OperatingSystem.IsMacOS() ? "-" + architecture : ""));
        var python = Path.Combine(venv, OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "");
        File.WriteAllText(Path.Combine(root, "worker.py"), "old worker");
        File.WriteAllText(Path.Combine(root, $"ready-small-4B-{backend}-{architecture}.txt"),
            "B37107DB793DD7DC4573CEB84A2C78D533650AA91B43B3B868B63CDF8604BF8F");
        try
        {
            using var runtime = new LocalDictationRuntime(root);
            Assert.False(runtime.IsReady("small", "4B", backend));
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

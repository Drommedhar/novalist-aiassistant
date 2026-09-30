using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Xunit;

/// <summary>Opt-in model test. Uses generated WAV fixtures, never the microphone.
/// Set NOVALIST_DICTATION_INTEGRATION=1, NOVALIST_DICTATION_FIXTURES to a folder
/// containing en.wav/de.wav, and PYTHONPATH to tests/offline_guard. Prepare the
/// default models through the app first; this test never downloads anything.</summary>
public class DictationOfflineIntegrationTests
{
    public static bool Enabled => Environment.GetEnvironmentVariable("NOVALIST_DICTATION_INTEGRATION") == "1";

    [Fact(SkipUnless = nameof(Enabled), Skip = "Opt in with prepared models, generated audio fixtures and the offline network guard.")]
    public async Task EnglishAndGermanAudioBecomeDialogueWithoutNetworkAndCancellationCanRestart()
    {
        var fixtures = Environment.GetEnvironmentVariable("NOVALIST_DICTATION_FIXTURES")!;
        Assert.Contains("offline_guard", Environment.GetEnvironmentVariable("PYTHONPATH") ?? "");
        using var runtime = new LocalDictationRuntime();
        var settings = new AiSettings { DictationAcceleration = Environment.GetEnvironmentVariable("NOVALIST_DICTATION_ACCELERATION") ?? "auto" };
        Assert.True(runtime.IsReady(settings.DictationModel, settings.DictationDialogueModel, settings.DictationAcceleration));
        var service = new DictationService(runtime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        foreach (var language in new[] { "en", "de" })
        {
            var transcript = await service.TranscribeAsync(settings,
                await File.ReadAllBytesAsync(Path.Combine(fixtures, language + ".wav"), timeout.Token),
                "audio/wav", language, timeout.Token);
            Assert.Contains(language == "en" ? "door" : "Tür", transcript);
            var segments = await service.DetectDialogueAsync(settings, transcript, language, "", timeout.Token);
            Assert.Contains(segments, s => s.Kind == "narration");
            Assert.Contains(segments, s => s.Kind == "dialogue");
            Assert.Contains(segments, s => s.Kind == "attribution"
                && s.Text.Contains(language == "en" ? "said" : "sagte"));
            var indirect = language == "en"
                ? "She said that the train would be late. He looked at his watch."
                : "Sie sagte, dass der Zug zu spät kommen würde. Er schaute auf seine Uhr.";
            var narration = await service.DetectDialogueAsync(settings, indirect, language, "", timeout.Token);
            Assert.All(narration, s => Assert.Equal("narration", s.Kind));
        }
        // Stop real native inference mid-request. A subsequent clip must be
        // handled by a fresh process, with no stale response from the old one.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DetectDialogueAsync(settings,
            "The door opened. Please wait, she said. I will come back tomorrow.", "en", "", cancel.Token));
        var retry = await service.TranscribeAsync(settings,
            await File.ReadAllBytesAsync(Path.Combine(fixtures, "en.wav"), timeout.Token), "audio/wav", "en", timeout.Token);
        Assert.Contains("door", retry);
    }
}

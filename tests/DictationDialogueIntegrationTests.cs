using System.Text.RegularExpressions;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Xunit;

/// <summary>Text-only regression test against installed models; no downloads or microphone.
/// Set NOVALIST_DICTATION_INTEGRATION=1 and PYTHONPATH to tests/offline_guard.
/// NOVALIST_DICTATION_ACCELERATION selects the prepared backend (default: auto).</summary>
public class DictationDialogueIntegrationTests(ITestOutputHelper output)
{
    public static bool Enabled => DictationOfflineIntegrationTests.Enabled;

    [Fact(SkipUnless = nameof(Enabled), Skip = "Opt in with prepared models and the offline network guard.")]
    public async Task UnquotedQuestionsKeepNarrationSpeechAndTagsSeparate()
    {
        Assert.Contains("offline_guard", Environment.GetEnvironmentVariable("PYTHONPATH") ?? "");
        using var runtime = new LocalDictationRuntime();
        var settings = new AiSettings
        {
            DictationAcceleration = Environment.GetEnvironmentVariable("NOVALIST_DICTATION_ACCELERATION") ?? "auto"
        };
        Assert.True(runtime.IsReady(settings.DictationModel, settings.DictationDialogueModel, settings.DictationAcceleration));
        var service = new DictationService(runtime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        (string Language, string Context, DictationSegment[] Expected)[] passages =
        [
            ("de", "", [
                new("Mit vorsichtigen, langsamen und tapsenden Schritten bewegte sich Liam langsam und vorsichtig die Treppe hinunter in Richtung Küche. Plötzlich, während er noch in seinen Gedanken war, sah er einen hellen, weißen Blitz vor seinen Augen, der ihn erschrocken zurückließ.", "narration"),
                new("Was zum Mama?", "dialogue", true),
                new("sagte er.", "attribution"),
                new("Vielen Dank.", "dialogue")]),
            ("en", "", [
                new("Liam walked down the stairs. A bright flash startled him.", "narration"),
                new("What was that?", "dialogue", true),
                new("he said.", "attribution"),
                new("Thank you.", "dialogue")]),
            // Audio pauses can put the narration into an earlier recording chunk.
            ("de", "Liam ging die Treppe hinunter. Ein heller Blitz erschreckte ihn.", [
                new("Was zum Mama?", "dialogue", true),
                new("sagte er.", "attribution"),
                new("Vielen Dank.", "dialogue")]),
            ("de", "", [
                new("Sie sagte, dass der Zug zu spät kommen würde. Er schaute auf seine Uhr.", "narration")]),
            ("en", "", [
                new("She said that the train would be late. He looked at his watch.", "narration")])
        ];
        foreach (var (language, context, expected) in passages)
        {
            var transcript = string.Join(" ", expected.Select(s => s.Text));
            output.WriteLine($"{settings.DictationAcceleration}: {transcript}");
            var actual = await service.DetectDialogueAsync(settings, transcript, language, context, timeout.Token);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(actual));
            // Accept different punctuation or narration segment sizes, but every
            // original word must receive the right role. Merely finding some
            // dialogue would miss swallowed tags and speech labelled attribution.
            Assert.Equal(ClassifiedWords(expected), ClassifiedWords(actual));
            Assert.All(actual.Where(s => s.Kind == "attribution"), s => Assert.False(s.NewParagraph));
            var continuation = actual.Last();
            if (continuation.Kind == "dialogue") Assert.False(continuation.NewParagraph);
        }
    }

    private static IEnumerable<(string Word, string Kind)> ClassifiedWords(IEnumerable<DictationSegment> segments)
        => segments.SelectMany(s => Regex.Matches(s.Text, @"[\p{L}\p{M}\p{N}]+")
            .Select(m => (m.Value.ToUpperInvariant(), s.Kind)));

    [Fact(SkipUnless = nameof(Enabled), Skip = "Opt in with prepared models and the offline network guard.")]
    public async Task FollowingSpeechAndActionsKeepTheirSpeakerAcrossChunks()
    {
        Assert.Contains("offline_guard", Environment.GetEnvironmentVariable("PYTHONPATH") ?? "");
        using var runtime = new LocalDictationRuntime();
        var settings = new AiSettings
        {
            DictationAcceleration = Environment.GetEnvironmentVariable("NOVALIST_DICTATION_ACCELERATION") ?? "auto"
        };
        var service = new DictationService(runtime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        (string Language, string Context, DictationSegment[] Expected)[] passages =
        [
            ("en", "", [new("Stay here,", "dialogue", true), new("Maya said.", "attribution"),
                new("I will fetch the key.", "dialogue"), new("She opened the drawer.", "narration"),
                new("Tom stepped outside.", "narration", true)]),
            ("de", "", [new("Bleib hier,", "dialogue", true), new("sagte Lena.", "attribution"),
                new("Ich hole den Schlüssel.", "dialogue"), new("Sie öffnete die Schublade.", "narration"),
                new("Paul ging nach draußen.", "narration", true)]),
            ("en", "“Stay here,” Maya said.", [new("I will fetch the key.", "dialogue"),
                new("She opened the drawer.", "narration"), new("Tom stepped outside.", "narration", true)]),
            ("de", "„Wohin gehst du?“, fragte Lena.", [new("Zum Bahnhof,", "dialogue", true),
                new("antwortete Paul.", "attribution"), new("Ich muss meinen Bruder abholen.", "dialogue")])
        ];
        foreach (var (language, context, expected) in passages)
        {
            var transcript = string.Join(" ", expected.Select(s => s.Text));
            var actual = await service.DetectDialogueAsync(settings, transcript, language, context, timeout.Token);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(actual));
            Assert.Equal(ClassifiedWords(expected), ClassifiedWords(actual));
            static IEnumerable<int> Paragraphs(IEnumerable<DictationSegment> segments)
            {
                var word = 0;
                foreach (var segment in segments)
                {
                    if (segment.NewParagraph) yield return word;
                    word += Regex.Matches(segment.Text, @"[\p{L}\p{M}\p{N}]+").Count;
                }
            }
            Assert.Equal(Paragraphs(expected), Paragraphs(actual));
        }
    }
}

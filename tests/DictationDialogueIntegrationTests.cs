using System.Text.RegularExpressions;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Xunit;

/// <summary>Text-only regression test against installed models; no downloads or microphone.
/// Set NOVALIST_DICTATION_INTEGRATION=1 and PYTHONPATH to tests/offline_guard.
/// NOVALIST_DICTATION_ACCELERATION selects the prepared backend (default: auto).
/// NOVALIST_DICTATION_SPEECH_MODEL selects the prepared speech model (default: small).</summary>
public class DictationDialogueIntegrationTests(ITestOutputHelper output)
{
    public static bool Enabled => DictationOfflineIntegrationTests.Enabled;

    private static AiSettings PreparedSettings => new()
    {
        DictationModel = Environment.GetEnvironmentVariable("NOVALIST_DICTATION_SPEECH_MODEL") ?? "small",
        DictationAcceleration = Environment.GetEnvironmentVariable("NOVALIST_DICTATION_ACCELERATION") ?? "auto"
    };

    [Fact(SkipUnless = nameof(Enabled), Skip = "Opt in with prepared models and the offline network guard.")]
    public async Task UnquotedQuestionsKeepNarrationSpeechAndTagsSeparate()
    {
        Assert.Contains("offline_guard", Environment.GetEnvironmentVariable("PYTHONPATH") ?? "");
        using var runtime = new LocalDictationRuntime();
        var settings = PreparedSettings;
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

    private static IEnumerable<int> Paragraphs(IEnumerable<DictationSegment> segments)
    {
        var word = 0;
        foreach (var segment in segments)
        {
            if (segment.NewParagraph) yield return word;
            word += Regex.Matches(segment.Text, @"[\p{L}\p{M}\p{N}]+").Count;
        }
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = "Opt in with prepared models and the offline network guard.")]
    public async Task FollowingSpeechAndActionsKeepTheirSpeakerAcrossChunks()
    {
        Assert.Contains("offline_guard", Environment.GetEnvironmentVariable("PYTHONPATH") ?? "");
        using var runtime = new LocalDictationRuntime();
        var settings = PreparedSettings;
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
            Assert.Equal(Paragraphs(expected), Paragraphs(actual));
        }
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = "Opt in with prepared models and the offline network guard.")]
    public async Task HeardVoiceTagsKeepContinuedSpeechSeparateFromSceneNarration()
    {
        Assert.Contains("offline_guard", Environment.GetEnvironmentVariable("PYTHONPATH") ?? "");
        using var runtime = new LocalDictationRuntime();
        var settings = PreparedSettings;
        Assert.True(runtime.IsReady(settings.DictationModel, settings.DictationDialogueModel, settings.DictationAcceleration));
        var service = new DictationService(runtime);
        // This batch has more cases than the smaller dialogue regressions;
        // quantized CPU inference needs a longer overall test deadline.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        DictationSegment[] gatePassage =
        [
            new("Langsam öffnete sich das Tor vor den Mauern.", "narration"),
            new("Tretet hinein, werte neue Schüler", "dialogue", true),
            new("hörte man eine Stimme laut durch die Hallen rufen.", "attribution"),
            new("Wir haben euch alle bereits erwartet.", "dialogue"),
            new("Langsam und vorsichtig begann Liam seinen Weg durch das große, hölzerne Tor.", "narration", true)
        ];
        var transcript = string.Join(" ", gatePassage.Select(s => s.Text));
        (string Language, string Context, string Transcript, DictationSegment[] Expected)[] passages =
        [
            ("de", "", transcript, gatePassage),
            ("de", "", transcript.Replace("Schüler hörte", "Schüler, hörte"), gatePassage),
            // Copied quotes must not hide a reporting clause inside the speech.
            ("de", "", gatePassage[0].Text + " »" + gatePassage[1].Text + ", "
                + gatePassage[2].Text.TrimEnd('.') + "«, "
                + string.Join(" ", gatePassage.Skip(3).Select(s => s.Text)), gatePassage),
            ("de", "", transcript.Replace("Schüler hörte", "Schüler, hörte")
                .Replace("rufen. Wir", "rufen, Wir"), gatePassage),
            ("de", gatePassage[0].Text, string.Join(" ", gatePassage.Skip(1).Select(s => s.Text)), gatePassage[1..]),
            // An 800 ms recording pause can split the reporting clause itself.
            ("de", gatePassage[0].Text, gatePassage[1].Text + ", hörte man eine Stimme",
                [gatePassage[1], new("hörte man eine Stimme", "attribution")]),
            ("de", "»Tretet hinein, werte neue Schüler«, hörte man eine Stimme",
                "laut durch die Hallen rufen. " + string.Join(" ", gatePassage.Skip(3).Select(s => s.Text)),
                [new("laut durch die Hallen rufen.", "attribution"), .. gatePassage[3..]]),
            ("de", "»Tretet hinein, werte neue Schüler«, hörte man eine Stimme laut durch die Hallen rufen.",
                string.Join(" ", gatePassage.Skip(3).Select(s => s.Text)), gatePassage[3..]),
            ("de", "»Tretet hinein, werte neue Schüler.«",
                string.Join(" ", gatePassage.Skip(2).Select(s => s.Text)), gatePassage[2..]),
            ("en", "", "The doors creaked open. Enter, new students, a voice called from deep within the hall. We have been expecting you. Leo crossed the threshold.",
                [new("The doors creaked open.", "narration"), new("Enter, new students,", "dialogue", true),
                 new("a voice called from deep within the hall.", "attribution"),
                 new("We have been expecting you.", "dialogue"), new("Leo crossed the threshold.", "narration", true)]),
            ("de", "", "Liam hörte eine Stimme laut durch die Hallen rufen. Er konnte die Worte nicht verstehen.",
                [new("Liam hörte eine Stimme laut durch die Hallen rufen. Er konnte die Worte nicht verstehen.", "narration")]),
            ("de", "", "Man hörte eine Stimme rufen, dass die Schüler erwartet wurden. Liam ging weiter.",
                [new("Man hörte eine Stimme rufen, dass die Schüler erwartet wurden. Liam ging weiter.", "narration")]),
            ("en", "", "Leo heard a voice calling from the hall. He could not make out the words.",
                [new("Leo heard a voice calling from the hall. He could not make out the words.", "narration")])
        ];
        foreach (var (language, context, text, expected) in passages)
        {
            output.WriteLine($"{language}, context: {context}, transcript: {text}");
            var actual = await service.DetectDialogueAsync(settings, text, language, context, timeout.Token);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(actual));
            Assert.Equal(ClassifiedWords(expected), ClassifiedWords(actual));
            if (expected.Any(s => s.Kind == "dialogue"))
                Assert.Equal(Paragraphs(expected), Paragraphs(actual));
        }
    }
}

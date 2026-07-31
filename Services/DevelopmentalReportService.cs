using System.Text;
using Novalist.Sdk;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>One heading of the report, and what it is asked to find.</summary>
/// <param name="Key">
/// Stable and unlocalised. The report is written once and re-read for months;
/// its sections have to be the same sections every time or two reports cannot
/// be compared.
/// </param>
public sealed record ReportSection(string Key, string Brief);

/// <summary>What a report pass produced.</summary>
public sealed record DevelopmentalReport(string? ResearchItemId, int Sections, string? Error);

/// <summary>
/// One report on the whole book, with the same headings every time.
///
/// Story Analysis returns free-form findings under four checks, per scene,
/// and nothing that adds up to a view of the book. A writer finishing a draft
/// wants the other thing: does the plot hold, do the arcs land, where does it
/// sag, what would I fix first - as a document they can read away from the
/// screen and still have next month.
///
/// So the headings are fixed rather than whatever the model felt like
/// producing. A report whose shape changes between runs cannot be compared with
/// the last one, which is most of what a second draft is for.
///
/// It lands on the research shelf as a note. That is where the writer's own
/// documents live, it is versioned and exportable like any other, and it needed
/// no new surface to put it in.
/// </summary>
public sealed class DevelopmentalReportService
{
    /// <summary>
    /// How much of the book reaches the model. A synopsis-level pass rather than
    /// the prose: a whole novel does not fit, and asking about structure from
    /// scene summaries is the right altitude anyway.
    /// </summary>
    private const int MaxContextChars = 40000;

    public static IReadOnlyList<ReportSection> Sections { get; } =
    [
        new("premise",
            "What this book is about, in two or three sentences, as somebody who has read it "
            + "would say it - not as the blurb would."),
        new("plot",
            "Whether the plot holds: what starts it, what escalates, what turns it, how it "
            + "resolves. Name the places where a step is missing or where something happens "
            + "because the story needs it to rather than because of what came before."),
        new("characters",
            "Each significant character: what they want, what it costs them, and whether they "
            + "are different by the end. Say plainly when somebody does not change and whether "
            + "that is a choice or an oversight."),
        new("pacing",
            "Where the book moves and where it sags, by chapter or part. Be specific about "
            + "which stretch, and say what is taking the time there."),
        new("conflict",
            "What is actually in opposition, and whether the opposition is strong enough. Note "
            + "any stretch where nobody is being resisted."),
        new("theme",
            "What the book keeps returning to, drawn from what is on the page rather than what "
            + "it might be trying to say. Note where the theme is stated outright rather than "
            + "dramatised."),
        new("continuity",
            "Facts that do not line up: chronology, what characters know and when, details that "
            + "change. Only contradictions, not weaknesses."),
        new("revision",
            "The five or six things to fix first, in order, each one an action rather than an "
            + "observation. This is the section the writer will work from."),
    ];

    private readonly AiService _ai;
    private readonly IHostServices _host;
    private readonly IExtensionLocalization _loc;

    public DevelopmentalReportService(AiService ai, IHostServices host, IExtensionLocalization loc)
    {
        _ai = ai;
        _host = host;
        _loc = loc;
    }

    /// <summary>
    /// Writes the report and files it. One model call per section, so a long
    /// pass reports as it goes and can be stopped - and so a section that comes
    /// back badly does not spoil the seven that did not.
    /// </summary>
    public async Task<DevelopmentalReport> BuildAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var context = BuildContext();
        if (context.Length < 200)
            return new DevelopmentalReport(null, 0, _loc.T("report.tooLittle"));

        var document = new StringBuilder();
        var written = 0;
        foreach (var section in Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var heading = _loc.T($"report.section.{section.Key}");
            progress?.Report(heading);

            string answer;
            try
            {
                var result = await _ai.GenerateChatAsync(
                    [
                        new SdkAiChatMessage { Role = "system", Content = SystemPrompt(section) },
                        new SdkAiChatMessage { Role = "user", Content = context },
                    ],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                answer = (result.Response ?? string.Empty).Trim();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            document.Append("## ").AppendLine(heading).AppendLine();
            // A heading with nothing under it is honest: it says the pass ran
            // and found nothing to say, where a silently dropped section reads
            // as a report that never covered it.
            document.AppendLine(answer.Length > 0 ? answer : _loc.T("report.sectionEmpty")).AppendLine();
            if (answer.Length > 0) written++;
        }

        if (written == 0) return new DevelopmentalReport(null, 0, _loc.T("report.failed"));

        var id = await _host.ResearchService.SaveAsync(new ResearchItemInfo
        {
            Title = _loc.T("report.title"),
            Type = "Note",
            Content = document.ToString(),
            Tags = ["report"],
        }).ConfigureAwait(false);

        return new DevelopmentalReport(id, written, null);
    }

    private string SystemPrompt(ReportSection section) =>
        "You are a developmental editor writing one section of a report on a finished draft. "
        + "You are given the book as chapter and scene summaries with their metadata.\n\n"
        + section.Brief
        + "\n\nWrite that section and nothing else - no heading, no preamble, no summary of the "
        + "book, no notes about the other sections. Be specific: name the chapter or scene you "
        + "mean. Draw only on what you were given; where it is thin, say what you cannot tell "
        + "from it rather than filling the gap. A few hundred words at most.\n\n"
        + $"Write in {_ai.LanguageName}.";

    /// <summary>
    /// The book at synopsis altitude: chapters, scenes, their synopses and what
    /// the writer recorded about each. The prose itself would not fit, and
    /// structure is not a question you answer from sentences.
    /// </summary>
    private string BuildContext()
    {
        var builder = new StringBuilder();
        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            if (builder.Length > MaxContextChars) break;
            builder.Append("# ").AppendLine(chapter.Title);

            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                if (builder.Length > MaxContextChars) break;
                builder.Append("- ").Append(scene.Title);

                var synopsis = _host.ProjectService
                    .GetSceneSynopsisAsync(chapter.Guid, scene.Id)
                    .GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(synopsis))
                    builder.Append(": ").Append(synopsis.Trim());
                builder.AppendLine();
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }
}

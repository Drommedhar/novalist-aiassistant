using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Generates a concise, encyclopedic summary for a Codex entity from the
/// deterministic dossier the Wiki assembles, by prompting the configured AI
/// model. The result is returned to the host, which caches it — this service
/// does not persist anything itself.
/// </summary>
public sealed class ArticleGeneratorService
{
    private const int MaxContextChars = 12000;

    private readonly AiService _ai;
    private readonly IExtensionLocalization _loc;

    public ArticleGeneratorService(AiService ai, IExtensionLocalization loc)
    {
        _ai = ai;
        _loc = loc;
    }

    public async Task<ArticleGenerationResult> GenerateAsync(
        ArticleGenerationRequest request, CancellationToken cancellationToken)
    {
        if (!await _ai.IsServerRunningAsync().ConfigureAwait(false))
            return new ArticleGenerationResult { Error = _loc.T("article.noModel") };

        var context = request.Context;
        if (context.Length > MaxContextChars) context = context[..MaxContextChars];

        var section = request.SectionTitle?.Trim() ?? string.Empty;
        var messages = new List<SdkAiChatMessage>
        {
            new()
            {
                Role = "system",
                Content = section.Length > 0 ? SectionPrompt(request, section) : SummaryPrompt(request),
            },
            new() { Role = "user", Content = UserMessage(request, context, section) },
        };

        try
        {
            var result = await _ai.GenerateChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            var summary = (result.Response ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(summary)
                ? new ArticleGenerationResult { Error = _loc.T("article.emptyResult") }
                : new ArticleGenerationResult { Summary = summary };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ArticleGenerationResult { Error = string.Format(_loc.T("article.failedReason"), ex.Message) };
        }
    }

    private string SummaryPrompt(ArticleGenerationRequest request) =>
        $"You write a concise, encyclopedic summary of a fictional {request.TypeKey} from a novel, "
        + "in the style of a Wikipedia lead - one to three short paragraphs. Explain who or what the subject "
        + "is and its role in the story, drawing only on the dossier provided. Do not invent facts. "
        + "Refer to the subject by their given name or full name, never by family name alone - relatives "
        + "share a surname, so a bare surname is ambiguous. Treat a family or group name as a group, not a person. "
        + $"No headings, no quotes, no commentary, third person. Respond in {_ai.LanguageName}.";

    /// <summary>
    /// One section of somebody's Codex entry, not a summary of the whole thing.
    ///
    /// The heading is the writer's own - "Backstory", "How they speak", "What
    /// the villagers say about him" - and is the clearest statement there is of
    /// what belongs underneath it. So it is the instruction rather than a hint,
    /// and the answer must not restate the rest of the entry: a section that
    /// re-summarises the dossier is the summary again under a different title.
    /// </summary>
    private string SectionPrompt(ArticleGenerationRequest request, string section)
    {
        var prompt =
            $"You write one section of a reference entry for a fictional {request.TypeKey} in a novel. "
            + $"The section is headed \"{section}\", and that heading is your instruction: write what "
            + "belongs under it and nothing else.\n\n"
            + "Draw only on the dossier. Where it is silent, stay silent - an invented fact in a "
            + "reference entry is worse than a short one, because it gets read later as something the "
            + "author decided. Do not restate the rest of the entry; the reader has it above and below "
            + "this section.\n\n"
            + "Refer to the subject by their given name or full name, never by family name alone - "
            + "relatives share a surname. Two or three short paragraphs at most. No heading (the "
            + "section already has one), no preamble, no commentary. Plain prose or light Markdown.";

        // A re-roll that cannot see what it is replacing hands back the same
        // paragraph with the clauses in a different order.
        if (!string.IsNullOrWhiteSpace(request.SectionContent))
        {
            prompt += "\n\nThe section already says something and the author has asked for another "
                + "attempt, so they did not want that one. Take a genuinely different angle: a "
                + "different aspect, a different level of detail, a different opening. Do not "
                + "paraphrase it back.";
        }

        return prompt + $"\n\nRespond in {_ai.LanguageName}.";
    }

    private static string UserMessage(ArticleGenerationRequest request, string context, string section)
    {
        var message = $"Subject: {request.EntityName}\n\nDOSSIER:\n{context}";
        if (section.Length == 0) return message;

        message += $"\n\nSECTION TO WRITE: {section}";
        if (!string.IsNullOrWhiteSpace(request.SectionContent))
            message += $"\n\nWHAT IT SAYS NOW (rejected):\n{request.SectionContent}";
        return message;
    }
}

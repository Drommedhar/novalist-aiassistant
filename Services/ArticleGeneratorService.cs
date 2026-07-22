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

        var sys = $"You write a concise, encyclopedic summary of a fictional {request.TypeKey} from a novel, "
            + "in the style of a Wikipedia lead — one to three short paragraphs. Explain who or what the subject "
            + "is and its role in the story, drawing only on the dossier provided. Do not invent facts. "
            + "Refer to the subject by their given name or full name, never by family name alone — relatives "
            + "share a surname, so a bare surname is ambiguous. Treat a family or group name as a group, not a person. "
            + $"No headings, no quotes, no commentary, third person. Respond in {_ai.LanguageName}.";
        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = sys },
            new() { Role = "user", Content = $"Subject: {request.EntityName}\n\nDOSSIER:\n{context}" },
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
}

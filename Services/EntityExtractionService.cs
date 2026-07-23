using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Reads a passage of the manuscript and proposes Codex entries for the people,
/// places, and things it mentions that the project does not know yet. Returns
/// proposals only — the host owns the review UI and every write, so a bad
/// suggestion can never touch the project on its own.
/// </summary>
public sealed class EntityExtractionService
{
    private const int MaxContextChars = 12000;
    private const int MaxProposals = 25;

    private readonly AiService _ai;
    private readonly IExtensionLocalization _loc;

    public EntityExtractionService(AiService ai, IExtensionLocalization loc)
    {
        _ai = ai;
        _loc = loc;
    }

    public async Task<EntityExtractionResult> ExtractAsync(
        EntityExtractionRequest request, CancellationToken cancellationToken)
    {
        if (!await _ai.IsServerRunningAsync().ConfigureAwait(false))
            return new EntityExtractionResult { Error = _loc.T("extract.noModel") };

        var context = request.Context;
        if (context.Length > MaxContextChars) context = context[..MaxContextChars];

        var types = string.Join(", ", request.AvailableTypeKeys);
        var known = request.KnownNames.Count > 0
            ? string.Join(", ", request.KnownNames)
            : "(none)";

        var sys =
            "You extract worldbuilding entries from a passage of a novel. Identify named people, places, "
            + "objects, and lore concepts that appear in the passage. Return ONLY a JSON array, no prose and "
            + "no code fence. Each element is an object with exactly these keys: \"typeKey\", \"name\", \"detail\". "
            + $"\"typeKey\" must be one of: {types}. \"name\" is the name exactly as written in the passage. "
            + "\"detail\" is one short sentence on what the passage says about it. "
            + "Omit anything already listed as known. Omit pronouns, common nouns, and generic descriptions. "
            + "If nothing qualifies, return []. "
            + $"Write \"detail\" in {_ai.LanguageName}.";

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = sys },
            new() { Role = "user", Content = $"KNOWN (do not propose these): {known}\n\nPASSAGE:\n{context}" },
        };

        try
        {
            var result = await _ai
                .GenerateChatAsync(messages, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var proposals = Parse(result.Response ?? string.Empty);
            return new EntityExtractionResult { Proposals = proposals };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EntityExtractionResult
            {
                Error = string.Format(_loc.T("extract.failedReason"), ex.Message)
            };
        }
    }

    /// <summary>Parses the model's JSON array, tolerating a code fence or stray
    /// prose around it. Malformed output yields no proposals rather than an
    /// error — the host simply reports that nothing was found.</summary>
    internal static List<EntityProposal> Parse(string response)
    {
        var proposals = new List<EntityProposal>();
        var json = ExtractJsonArray(response);
        if (json == null) return proposals;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return proposals;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (proposals.Count >= MaxProposals) break;
                if (element.ValueKind != JsonValueKind.Object) continue;

                var name = ReadString(element, "name");
                var typeKey = ReadString(element, "typeKey");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(typeKey)) continue;

                proposals.Add(new EntityProposal
                {
                    TypeKey = typeKey.Trim(),
                    Name = name.Trim(),
                    Detail = ReadString(element, "detail")?.Trim() ?? string.Empty
                });
            }
        }
        catch (JsonException)
        {
            // Model returned something that is not valid JSON — treat as no findings.
        }
        return proposals;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Isolates the outermost JSON array in the response.</summary>
    private static string? ExtractJsonArray(string response)
    {
        var start = response.IndexOf('[');
        var end = response.LastIndexOf(']');
        return start >= 0 && end > start ? response[start..(end + 1)] : null;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Extensions.AiAssistant.Models;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Wraps <see cref="AiService"/> to extract structured per-scene knowledge for a
/// single character via a JSON-mode prompt.
/// </summary>
public sealed class KnowledgeBuilder
{
    private readonly AiService _ai;

    public KnowledgeBuilder(AiService ai)
    {
        _ai = ai;
    }

    public async Task<CharacterSceneKnowledge> BuildAsync(
        CharacterInfo character,
        string sceneText,
        string sceneTitle,
        string chapterTitle,
        CancellationToken cancellationToken = default)
    {
        var sysPrompt = $$"""
        You analyse novel scenes from the perspective of a single character. Read
        the scene and decide whether the character "{{character.DisplayName}}" is
        actually present and active in it (not merely mentioned in someone else's
        thoughts, dialogue, or narration). If they are present, extract ONLY what
        they personally experienced, learned, or said.

        Output STRICT JSON matching this schema (no prose, no fences):
        {
          "present": true,
          "observed": ["..."],
          "learned": ["..."],
          "said": ["..."],
          "emotion": "...",
          "uncertain": ["..."]
        }

        Rules:
        - "present" = true ONLY if the character is physically/virtually in the scene OR is the POV/narrator. Set false when they are only mentioned, remembered, or talked about by others.
        - When "present" is false, set every other field to empty ([] or "").
        - "observed" = things the character saw / heard directly in the scene.
        - "learned" = facts they were told or deduced.
        - "said" = statements they made (intent, claims, lies). Use direct paraphrase.
        - "emotion" = single short phrase describing their emotional state at end of scene.
        - "uncertain" = open questions or things the character is unsure of.
        - Be concise: each bullet a single short sentence.
        - Respond in {{_ai.LanguageName}}.
        """;

        var userPrompt = $$"""
        CHARACTER:
        Name: {{character.DisplayName}}
        Role: {{character.Role}}
        Aliases: {{string.Join(", ", character.Aliases)}}

        CHAPTER: {{chapterTitle}}
        SCENE: {{sceneTitle}}

        SCENE TEXT:
        {{sceneText}}
        """;

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = sysPrompt },
            new() { Role = "user", Content = userPrompt }
        };

        var result = await _ai.GenerateChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var json = ExtractJson(result.Response);
        return ParseKnowledge(json);
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "{}";

        // Strip ``` code fences if the model added them.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        // Locate first { ... last }.
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];

        return "{}";
    }

    private static CharacterSceneKnowledge ParseKnowledge(string json)
    {
        var k = new CharacterSceneKnowledge();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("present", out var pres) && pres.ValueKind is JsonValueKind.True or JsonValueKind.False)
                k.Present = pres.GetBoolean();

            k.Observed = ReadStringArray(root, "observed");
            k.Learned = ReadStringArray(root, "learned");
            k.Said = ReadStringArray(root, "said");
            k.Uncertain = ReadStringArray(root, "uncertain");
            if (root.TryGetProperty("emotion", out var em))
                k.Emotion = em.ValueKind == JsonValueKind.String ? em.GetString() ?? string.Empty : string.Empty;

            // Heuristic fallback: if model omitted "present" but content is empty,
            // treat as not present.
            if (!root.TryGetProperty("present", out _))
                k.Present = !k.IsEmpty;
        }
        catch
        {
            // Leave knowledge empty on parse failure; caller still records the hash
            // so we don't keep retrying a scene that the model can't parse.
        }
        return k;
    }

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
        }
        return list;
    }
}

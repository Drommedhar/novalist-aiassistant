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
    private readonly IHostServices _host;

    public KnowledgeBuilder(AiService ai, IHostServices host)
    {
        _ai = ai;
        _host = host;
    }

    public async Task<CharacterSceneKnowledge> BuildAsync(
        CharacterInfo character,
        string sceneText,
        string sceneTitle,
        string chapterTitle,
        string? chapterGuid,
        string? sceneId,
        CancellationToken cancellationToken = default)
    {
        CharacterDetailedInfo? detailed = null;
        try
        {
            detailed = await _host.EntityService.GetCharacterDetailedAsync(
                character.Id, chapterGuid, sceneId).ConfigureAwait(false);
        }
        catch
        {
            // Host resolution failure must not block extraction; fall back to
            // the lightweight CharacterInfo sheet below.
        }

        var characterSheet = detailed != null
            ? CharacterSheetBuilder.Build(detailed)
            : CharacterSheetBuilder.BuildFallback(character);

        var sysPrompt = $$"""
        You analyse novel scenes from the perspective of a single character. Read
        the scene and decide whether the character "{{character.DisplayName}}" is
        actually present and active in it (not merely mentioned in someone else's
        thoughts, dialogue, or narration). If they are present, extract a DEEP
        record of what they personally experienced — enough that another AI could
        later impersonate the character with full continuity.

        Use the CHARACTER SHEET below as binding reality when interpreting the
        scene: a six-year-old does not understand adult dialogue the way an
        adult would; a mute character cannot have said anything aloud; an
        unconscious or absent character is not "present". Honour age, physical
        state, secrets, and relationships from the sheet — they constrain what
        the character could observe, learn, say, or do in this scene.

        Output STRICT JSON matching this schema (no prose, no fences):
        {
          "present": true,
          "observed": ["..."],
          "learned": ["..."],
          "said": ["..."],
          "emotion": "...",
          "uncertain": ["..."],
          "location": "...",
          "companions": ["..."],
          "physicalState": "...",
          "goals": ["..."],
          "relationshipChanges": ["..."],
          "secrets": ["..."],
          "voiceNotes": "...",
          "inventoryChanges": ["..."]
        }

        Rules:
        - "present" = true ONLY if the character is physically/virtually in the scene OR is the POV/narrator. Set false when they are only mentioned, remembered, or talked about by others.
        - When "present" is false, set every other field to empty ([] or "").
        - "observed" = things the character saw / heard / felt directly.
        - "learned" = facts they were told or deduced.
        - "said" = statements they made (intent, claims, lies). Use direct paraphrase.
        - "emotion" = single short phrase describing their emotional state at end of scene.
        - "uncertain" = open questions or things the character is unsure of.
        - "location" = where they physically are during the scene.
        - "companions" = other named characters present with them.
        - "physicalState" = injuries, exhaustion, intoxication, illness, ability to speak/move, etc. by end of scene.
        - "goals" = short-term intentions they hold when the scene ends.
        - "relationshipChanges" = how feelings/trust toward others shifted in this scene (one entry per relationship).
        - "secrets" = secrets they protected, revealed, learned, or now must keep.
        - "voiceNotes" = how they speak/move in this scene — dialect, tone, mannerisms, whether they can speak at all.
        - "inventoryChanges" = items gained, lost, used, or now carrying.
        - Be specific and grounded in the actual scene text. Do not invent facts. If a field has nothing for this scene, return [] or "".
        - Each bullet should be a single short sentence.
        - Respond in {{_ai.LanguageName}}.
        """;

        var userPrompt = $$"""
        # CHARACTER SHEET
        {{characterSheet.TrimEnd()}}

        # SCENE
        Chapter: {{chapterTitle}}
        Scene: {{sceneTitle}}

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
            k.Companions = ReadStringArray(root, "companions");
            k.Goals = ReadStringArray(root, "goals");
            k.RelationshipChanges = ReadStringArray(root, "relationshipChanges");
            k.Secrets = ReadStringArray(root, "secrets");
            k.InventoryChanges = ReadStringArray(root, "inventoryChanges");

            string ReadStr(string name)
            {
                if (!root.TryGetProperty(name, out var v)) return string.Empty;
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            }
            k.Emotion = ReadStr("emotion");
            k.Location = ReadStr("location");
            k.PhysicalState = ReadStr("physicalState");
            k.VoiceNotes = ReadStr("voiceNotes");

            k.SchemaVersion = CharacterSceneKnowledge.CurrentSchemaVersion;

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

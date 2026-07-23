using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Reads one scene once and returns everything a pass over it can establish:
/// which entities it involves and whether each is actually present or only talked
/// about, what every present character perceived and learned, and any findings
/// worth showing the writer.
///
/// This replaces the previous arrangement, where each character cost its own call
/// (each re-sending the whole scene) and the analysis cost another on top. One
/// scene is now one call, which is where nearly all of the cold-start time went.
///
/// The prompt is deliberately model-agnostic: a plain JSON contract stated in the
/// message, no reliance on tool-calling, structured-output modes, reasoning
/// tokens, or any one runtime's extensions, and tolerant parsing of what comes
/// back. Whatever the writer points it at should be able to answer.
/// </summary>
public sealed class SceneAnalysisService
{
    private const int MaxContextChars = 24000;

    private readonly AiService _ai;
    private readonly IExtensionLocalization _loc;

    public SceneAnalysisService(AiService ai, IExtensionLocalization loc)
    {
        _ai = ai;
        _loc = loc;
    }

    /// <summary>The input for one scene. Everything here is assembled by the
    /// caller from the host; the service adds no project knowledge of its own.</summary>
    public sealed class SceneRequest
    {
        public string SceneId { get; init; } = string.Empty;
        public string ChapterGuid { get; init; } = string.Empty;
        public string ChapterTitle { get; init; } = string.Empty;
        public string SceneTitle { get; init; } = string.Empty;
        public string SceneText { get; init; } = string.Empty;

        /// <summary>Every Codex entry, so the model can recognise anything. We do
        /// not pre-filter this: name matching is unreliable and dropping an entity
        /// here would silently cost recall.</summary>
        public IReadOnlyList<EntitySummary> Entities { get; init; } = [];

        /// <summary>Names the writer explicitly `@`-mentioned in this scene.
        /// Author-confirmed, so the model is told to treat them as certain.</summary>
        public IReadOnlyList<string> ConfirmedMentions { get; init; } = [];

        /// <summary>Which finding categories to produce.</summary>
        public EnabledChecks Checks { get; init; } = new();
    }

    /// <summary>
    /// The stored record for a scene, analysing and saving it first when it is
    /// missing or out of date.
    ///
    /// This is the single door every feature goes through, which is what makes the
    /// work shared: whichever of story analysis, character knowledge or a
    /// background pass reaches a scene first pays for it, and the others get it for
    /// nothing. Returns null only when the scene needs analysing and the model
    /// cannot be reached.
    /// </summary>
    public async Task<SceneAnalysisRecord?> GetOrCreateAsync(
        IHostServices host, SceneRequest request, CancellationToken cancellationToken,
        Action<string>? onChunk = null, Action<string>? onThinkingChunk = null)
    {
        if (!await host.IsSceneAnalysisStaleAsync(request.SceneId, request.SceneText).ConfigureAwait(false))
            return await host.GetSceneAnalysisAsync(request.SceneId).ConfigureAwait(false);

        var record = await AnalyseAsync(request, cancellationToken, onChunk, onThinkingChunk)
            .ConfigureAwait(false);
        if (record != null)
            await host.SaveSceneAnalysisAsync(record, request.SceneText).ConfigureAwait(false);
        return record;
    }

    /// <summary>Builds the request for a scene, pulling the author-confirmed
    /// mentions from the host and turning them into names the model will recognise.</summary>
    public static async Task<SceneRequest> BuildRequestAsync(
        IHostServices host,
        string chapterGuid, string chapterTitle, string sceneId, string sceneTitle,
        string sceneText, IReadOnlyList<EntitySummary> entities, EnabledChecks checks)
    {
        var confirmedIds = await host.GetConfirmedMentionIdsAsync(chapterGuid, sceneId).ConfigureAwait(false);
        var confirmedNames = confirmedIds
            .Select(id => entities.FirstOrDefault(e => e.Id == id)?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();

        return new SceneRequest
        {
            SceneId = sceneId,
            ChapterGuid = chapterGuid,
            ChapterTitle = chapterTitle,
            SceneTitle = sceneTitle,
            SceneText = sceneText,
            Entities = entities,
            ConfirmedMentions = confirmedNames,
            Checks = checks
        };
    }

    /// <summary>Analyses one scene. Returns null when the model is unreachable;
    /// the caller then leaves the scene unanalysed rather than storing a blank.</summary>
    public async Task<SceneAnalysisRecord?> AnalyseAsync(
        SceneRequest request, CancellationToken cancellationToken,
        Action<string>? onChunk = null, Action<string>? onThinkingChunk = null)
    {
        if (!await _ai.IsServerRunningAsync().ConfigureAwait(false))
            return null;

        var text = request.SceneText;
        if (text.Length > MaxContextChars) text = text[..MaxContextChars];

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = BuildSystemPrompt(request) },
            new() { Role = "user", Content = BuildUserPrompt(request, text) },
        };

        var result = await _ai
            .GenerateChatAsync(messages, onChunk, onThinkingChunk: onThinkingChunk,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var record = Parse(result.Response ?? string.Empty, request);
        record.ModelId = _ai.ModelName ?? string.Empty;
        return record;
    }

    private string BuildSystemPrompt(SceneRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("You analyse one scene of a novel and reply with a single JSON object and nothing else. ");
        sb.Append("No prose, no explanation, no code fence.\n\n");
        sb.Append("The object has exactly these keys:\n");
        sb.Append("{\n");
        sb.Append("  \"entities\": [ { \"name\": string, \"type\": \"character\"|\"location\"|\"item\"|\"lore\", ");
        sb.Append("\"presence\": \"present\"|\"mentioned\", \"note\": string } ],\n");
        sb.Append("  \"characters\": [ { \"name\": string, \"presence\": \"present\"|\"mentioned\", ");
        sb.Append("\"observed\": [string], \"learned\": [string], \"said\": [string], \"uncertain\": [string], ");
        sb.Append("\"emotion\": string, \"location\": string, \"companions\": [string], ");
        sb.Append("\"physicalState\": string, \"goals\": [string], \"relationshipChanges\": [string], ");
        sb.Append("\"secrets\": [string], \"voiceNotes\": string, \"inventoryChanges\": [string] } ],\n");
        sb.Append("  \"findings\": [ { \"type\": \"reference\"|\"inconsistency\"|\"suggestion\", ");
        sb.Append("\"title\": string, \"description\": string, \"excerpt\": string, ");
        sb.Append("\"entityName\": string, \"entityType\": string } ]\n");
        sb.Append("}\n\n");

        sb.Append("Rules:\n");
        sb.Append("- \"presence\" is \"present\" when the entity is physically there in this scene, ");
        sb.Append("and \"mentioned\" when it is only referred to, remembered, or talked about. ");
        sb.Append("A character recalled or discussed did not witness the scene. Omit anything absent.\n");
        sb.Append("- Only list entities from the known list below, matched to their exact listed name, ");
        sb.Append("plus any clearly named person, place or object that is missing from it ");
        sb.Append("(use its name as written in the prose).\n");
        sb.Append("- \"characters\" describes only characters whose presence is \"present\". ");
        sb.Append("Attribute nothing to a character who was not there.\n");
        sb.Append("- Base everything strictly on this scene's text. Do not invent, and do not carry in ");
        sb.Append("knowledge from elsewhere in the story.\n");
        sb.Append("- Use empty arrays and empty strings rather than null. Return [] for a section with nothing to report.\n");

        // Without these, the model files everything under "observed" and leaves
        // "learned" empty — which is the field the character-knowledge features
        // actually read, since it is what a character now knows that they did not
        // know before this scene.
        sb.Append("\nWhat each character field means:\n");
        sb.Append("- \"observed\": what the character saw, heard or did first-hand here.\n");
        sb.Append("- \"learned\": information the character did NOT know before this scene and now does — ");
        sb.Append("a name, a fact, a place, someone's intention, a revelation. This is distinct from ");
        sb.Append("\"observed\": seeing a shed is observed, learning that it can be their hideout is learned. ");
        sb.Append("Most scenes teach a present character something; look for it before leaving this empty.\n");
        sb.Append("- \"said\": notable things the character said aloud.\n");
        sb.Append("- \"uncertain\": what the character suspects, doubts or misreads but does not know.\n");
        sb.Append("- \"emotion\": their dominant emotional state. \"location\": where they are. ");
        sb.Append("\"companions\": who is with them. \"physicalState\": injuries, exhaustion, appearance.\n");
        sb.Append("- \"goals\": what they want as of this scene. ");
        sb.Append("\"relationshipChanges\": how a bond shifted, and with whom.\n");
        sb.Append("- \"secrets\": what they conceal, or learned that others present did not. ");
        sb.Append("\"inventoryChanges\": items gained, lost or handed over. ");
        sb.Append("\"voiceNotes\": distinctive speech habits worth keeping consistent.\n");

        var checks = new List<string>();
        if (request.Checks.References) checks.Add("\"reference\" (something notable this scene establishes)");
        if (request.Checks.Inconsistencies) checks.Add("\"inconsistency\" (a contradiction with the known entries)");
        if (request.Checks.Suggestions) checks.Add("\"suggestion\" (a concrete improvement)");
        sb.Append(checks.Count > 0
            ? $"- \"findings\" may contain: {string.Join(", ", checks)}.\n"
            : "- Return an empty \"findings\" array.\n");

        sb.Append($"\nWrite all prose values in {_ai.LanguageName}.");
        return sb.ToString();
    }

    private static string BuildUserPrompt(SceneRequest request, string text)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CHAPTER: {request.ChapterTitle}");
        sb.AppendLine($"SCENE: {request.SceneTitle}");

        if (request.Entities.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("KNOWN ENTRIES:");
            foreach (var e in request.Entities)
            {
                var detail = string.IsNullOrWhiteSpace(e.Details) ? string.Empty : $" - {e.Details}";
                sb.AppendLine($"- [{e.Type}] {e.Name}{detail}");
            }
        }

        if (request.ConfirmedMentions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("The author explicitly tagged these in this scene, so they are certainly referenced "
                + "(decide for each whether it is present or only mentioned):");
            foreach (var name in request.ConfirmedMentions)
                sb.AppendLine($"- {name}");
        }

        sb.AppendLine();
        sb.AppendLine("SCENE TEXT:");
        sb.Append(text);
        return sb.ToString();
    }

    /// <summary>
    /// Turns the model's reply into a record. Anything unparseable yields an empty
    /// record rather than an error: a scene the model fumbled is better stored as
    /// "nothing found" than retried forever.
    /// </summary>
    internal static SceneAnalysisRecord Parse(string response, SceneRequest request)
    {
        var record = new SceneAnalysisRecord
        {
            SceneId = request.SceneId,
            ChapterGuid = request.ChapterGuid,
            ChapterTitle = request.ChapterTitle,
            SceneTitle = request.SceneTitle
        };

        var json = ExtractJsonObject(response);
        if (json == null) return record;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return record;
        }
        if (root.ValueKind != JsonValueKind.Object) return record;

        // name (lowercased) -> the known entry, so returned names resolve to ids.
        var known = new Dictionary<string, EntitySummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in request.Entities)
            known[e.Name] = e;

        foreach (var item in Array(root, "entities"))
        {
            var name = Str(item, "name");
            if (name.Length == 0) continue;
            var type = Str(item, "type");
            var resolved = known.TryGetValue(name, out var match) ? match : null;
            if (type.Length == 0 && resolved != null) type = resolved.Type;
            record.Entities.Add(new SceneEntityRef
            {
                Name = name,
                // Tie the reference back to the Codex entry when the name matches
                // one. A null id is meaningful: it marks something the prose names
                // but the Codex does not have yet, which is what feeds the
                // "create this entity" proposals.
                EntityId = string.IsNullOrEmpty(resolved?.Id) ? null : resolved.Id,
                EntityType = type,
                Presence = NormalizePresence(Str(item, "presence")),
                Note = Str(item, "note")
            });
        }

        foreach (var item in Array(root, "characters"))
        {
            var name = Str(item, "name");
            if (name.Length == 0) continue;
            var resolvedCharacter = known.TryGetValue(name, out var characterMatch)
                && characterMatch.Type == "character" ? characterMatch : null;
            record.Characters.Add(new SceneCharacterKnowledge
            {
                Name = name,
                CharacterId = string.IsNullOrEmpty(resolvedCharacter?.Id) ? null : resolvedCharacter.Id,
                Presence = NormalizePresence(Str(item, "presence")),
                Observed = StrList(item, "observed"),
                Learned = StrList(item, "learned"),
                Said = StrList(item, "said"),
                Uncertain = StrList(item, "uncertain"),
                Emotion = Str(item, "emotion"),
                Location = Str(item, "location"),
                Companions = StrList(item, "companions"),
                PhysicalState = Str(item, "physicalState"),
                Goals = StrList(item, "goals"),
                RelationshipChanges = StrList(item, "relationshipChanges"),
                Secrets = StrList(item, "secrets"),
                VoiceNotes = Str(item, "voiceNotes"),
                InventoryChanges = StrList(item, "inventoryChanges")
            });
        }

        foreach (var item in Array(root, "findings"))
        {
            var title = Str(item, "title");
            if (title.Length == 0) continue;
            record.Findings.Add(new CachedAiFinding
            {
                Type = Str(item, "type"),
                Title = title,
                Description = Str(item, "description"),
                Excerpt = Str(item, "excerpt"),
                EntityName = Str(item, "entityName"),
                EntityType = Str(item, "entityType")
            });
        }

        return record;
    }

    /// <summary>Maps whatever the model said to one of the three known values.
    /// Anything unrecognised is read as "mentioned" — the cautious choice, since
    /// it attributes no knowledge.</summary>
    private static string NormalizePresence(string value) => value.Trim().ToLowerInvariant() switch
    {
        "present" => ScenePresence.Present,
        "absent" => ScenePresence.Absent,
        _ => ScenePresence.Mentioned
    };

    private static IEnumerable<JsonElement> Array(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string Str(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static List<string> StrList(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return [.. value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()?.Trim() ?? string.Empty)
            .Where(s => s.Length > 0)];
    }

    /// <summary>Isolates the outermost JSON object, tolerating a code fence or
    /// stray commentary around it.</summary>
    internal static string? ExtractJsonObject(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        return start >= 0 && end > start ? response[start..(end + 1)] : null;
    }
}

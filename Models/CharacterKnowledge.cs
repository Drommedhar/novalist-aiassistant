using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Novalist.Extensions.AiAssistant.Models;

/// <summary>
/// What a character knows / experienced in a single scene. Stored under
/// <c>{extensionData}/knowledge/{characterId}.json</c> as a list of these entries.
/// </summary>
public sealed class CharacterSceneKnowledge
{
    [JsonPropertyName("sceneId")]
    public string SceneId { get; set; } = string.Empty;

    [JsonPropertyName("chapterGuid")]
    public string ChapterGuid { get; set; } = string.Empty;

    [JsonPropertyName("chapterTitle")]
    public string ChapterTitle { get; set; } = string.Empty;

    [JsonPropertyName("sceneTitle")]
    public string SceneTitle { get; set; } = string.Empty;

    /// <summary>Hash of the scene content used at generation time. Used to
    /// detect staleness without storing the full scene text.</summary>
    [JsonPropertyName("sceneContentHash")]
    public string SceneContentHash { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>True when the character actually played a meaningful role in this
    /// scene (the LLM decided so). False when only mentioned in passing or absent.
    /// Stored so we never reprocess the same scene+hash twice.</summary>
    [JsonPropertyName("present")]
    public bool Present { get; set; }

    /// <summary>Things the character directly perceived (saw, heard, did).</summary>
    [JsonPropertyName("observed")]
    public List<string> Observed { get; set; } = [];

    /// <summary>Things the character was told or learned indirectly.</summary>
    [JsonPropertyName("learned")]
    public List<string> Learned { get; set; } = [];

    /// <summary>Things said by the character (intent, claims, lies).</summary>
    [JsonPropertyName("said")]
    public List<string> Said { get; set; } = [];

    /// <summary>Emotional state at end of scene.</summary>
    [JsonPropertyName("emotion")]
    public string Emotion { get; set; } = string.Empty;

    /// <summary>Open questions / things the character is uncertain about.</summary>
    [JsonPropertyName("uncertain")]
    public List<string> Uncertain { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty
        => Observed.Count == 0 && Learned.Count == 0 && Said.Count == 0
           && Uncertain.Count == 0 && string.IsNullOrWhiteSpace(Emotion);
}

/// <summary>Per-character knowledge file, list of scene entries.</summary>
public sealed class CharacterKnowledgeFile
{
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = string.Empty;

    [JsonPropertyName("scenes")]
    public List<CharacterSceneKnowledge> Scenes { get; set; } = [];
}

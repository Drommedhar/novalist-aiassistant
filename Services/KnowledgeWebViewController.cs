using System.Text.Json;
using Novalist.Extensions.AiAssistant.Models;
using Novalist.Sdk.Models;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Bridge for the Character Knowledge view.
///
/// The scan used to be startable from nowhere and its output readable nowhere:
/// the data sat in per-character JSON files that only the roleplay prompt ever
/// touched. This view exists so a writer can run the scan and then actually read
/// what the model concluded, per character and per scene, and judge whether it
/// is right before trusting it in "Talk as character".
/// </summary>
public sealed class KnowledgeWebViewController : IWebViewController, IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IHostServices _host;
    private readonly AiAssistantExtension _extension;
    private readonly IExtensionLocalization _loc;
    private CancellationTokenSource? _scanCts;

    public event Action<string>? MessagePosted;

    public KnowledgeWebViewController(IHostServices host, AiAssistantExtension extension)
    {
        _host = host;
        _extension = extension;
        _loc = host.GetLocalization(extension.Id);
    }

    public async Task<string?> OnMessageAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "hydrate":
                // Records may have been written by another feature since this
                // panel last loaded, so re-opening it always re-reads them.
                _sceneCache = null;
                return await CharactersAsync().ConfigureAwait(false);

            case "selectCharacter":
                return await ScenesAsync(root.GetProperty("characterId").GetString() ?? string.Empty)
                    .ConfigureAwait(false);

            case "scan":
                _ = RunScanAsync();
                return null;

            case "cancelScan":
                _scanCts?.Cancel();
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Every scene in story order paired with its analysis record, if one exists.
    ///
    /// The records are the source of truth: any scene analysed by any feature
    /// already describes every character in it, so this view shows that work
    /// immediately rather than making the user run a scan to see data they
    /// already have. Cached for the panel's lifetime and dropped after a scan.
    /// </summary>
    private List<(ChapterInfo Chapter, SceneInfo Scene, SceneAnalysisRecord? Record)>? _sceneCache;

    private async Task<List<(ChapterInfo Chapter, SceneInfo Scene, SceneAnalysisRecord? Record)>>
        LoadScenesAsync()
    {
        if (_sceneCache != null) return _sceneCache;

        var list = new List<(ChapterInfo, SceneInfo, SceneAnalysisRecord?)>();
        foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                var record = await _host.GetSceneAnalysisAsync(scene.Id).ConfigureAwait(false);
                list.Add((chapter, scene, record));
            }
        }
        _sceneCache = list;
        return list;
    }

    /// <summary>Every character in name order, with how many scenes they are
    /// recorded as present in — so "not analysed yet" reads differently from
    /// "analysed, and they were in none of them".</summary>
    private async Task<string> CharactersAsync()
    {
        var characters = await _host.EntityService.LoadCharactersAsync().ConfigureAwait(false);
        var scenes = await LoadScenesAsync().ConfigureAwait(false);
        var service = _extension.KnowledgeService;
        var rows = new List<object>();

        foreach (var character in characters.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var analysed = 0;
            var present = 0;
            foreach (var (_, scene, record) in scenes)
            {
                var entry = record != null
                    ? CharacterKnowledgeService.ProjectFromRecord(record, character, string.Empty)
                    : await StoredEntryAsync(service, character.Id, scene.Id).ConfigureAwait(false);
                if (entry == null) continue;
                analysed++;
                if (entry.Present) present++;
            }
            rows.Add(new
            {
                id = character.Id,
                name = character.DisplayName,
                scenesKnown = present,
                scenesRecorded = analysed,
            });
        }

        return JsonSerializer.Serialize(new
        {
            type = "characters",
            characters = rows,
            // The view carries no English of its own; labels come from here.
            strings = new Dictionary<string, string>
            {
                ["scan"] = _loc.T("knowledge.scan"),
                ["stop"] = _loc.T("knowledge.stop"),
                ["loading"] = _loc.T("knowledge.loading"),
                ["selectCharacter"] = _loc.T("knowledge.selectCharacter"),
                ["noCharacters"] = _loc.T("knowledge.noCharacters"),
                ["notScanned"] = _loc.T("knowledge.notScanned"),
                ["presentIn"] = _loc.T("knowledge.presentIn"),
                ["noData"] = _loc.T("knowledge.noData"),
                ["notPresent"] = _loc.T("knowledge.notPresent"),
                ["untitledScene"] = _loc.T("knowledge.untitledScene"),
                ["model"] = _loc.T("knowledge.model"),
                ["starting"] = _loc.T("knowledge.starting"),
                ["stopping"] = _loc.T("knowledge.stopping"),
                ["error"] = _loc.T("knowledge.error"),
            }
        }, Json);
    }

    /// <summary>One row per analysed scene for a character, in story order.
    /// Absent scenes are included but flagged, because "the model decided they
    /// were not in this scene" is itself information.</summary>
    private async Task<string> ScenesAsync(string characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId))
            return JsonSerializer.Serialize(new { type = "scenes", scenes = Array.Empty<object>() }, Json);

        var characters = await _host.EntityService.LoadCharactersAsync().ConfigureAwait(false);
        var character = characters.FirstOrDefault(c =>
            string.Equals(c.Id, characterId, StringComparison.OrdinalIgnoreCase));
        if (character == null)
            return JsonSerializer.Serialize(new { type = "scenes", scenes = Array.Empty<object>() }, Json);

        var service = _extension.KnowledgeService;
        var rows = new List<object>();

        foreach (var (chapter, scene, record) in await LoadScenesAsync().ConfigureAwait(false))
        {
            // Prefer the shared record; fall back to a stored entry so knowledge
            // from an older scan is not hidden just because the scene has not
            // been re-analysed yet.
            var entry = record != null
                ? CharacterKnowledgeService.ProjectFromRecord(record, character, string.Empty)
                : await StoredEntryAsync(service, characterId, scene.Id).ConfigureAwait(false);
            if (entry == null) continue;   // scene never analysed — nothing to show

            rows.Add(new
            {
                sceneId = scene.Id,
                sceneTitle = scene.Title,
                chapterTitle = chapter.Title,
                present = entry.Present,
                modelId = entry.ModelId,
                generatedAt = entry.GeneratedAt,
                fields = Fields(entry),
            });
        }

        return JsonSerializer.Serialize(new { type = "scenes", scenes = rows }, Json);
    }

    private static async Task<CharacterSceneKnowledge?> StoredEntryAsync(
        CharacterKnowledgeService? service, string characterId, string sceneId)
    {
        if (service == null) return null;
        var file = await service.LoadAsync(characterId).ConfigureAwait(false);
        return file.Scenes.FirstOrDefault(s =>
            string.Equals(s.SceneId, sceneId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Flattens an entry into label/value pairs, dropping whatever the
    /// model had nothing to say about so the reader sees signal, not a grid of
    /// empty rows.</summary>
    private List<object> Fields(CharacterSceneKnowledge s)
    {
        var fields = new List<object>();

        void AddList(string key, List<string> values)
        {
            if (values.Count > 0)
                fields.Add(new { label = _loc.T($"knowledge.field.{key}"), values });
        }
        void AddText(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                fields.Add(new { label = _loc.T($"knowledge.field.{key}"), values = new[] { value } });
        }

        AddList("observed", s.Observed);
        AddList("learned", s.Learned);
        AddList("said", s.Said);
        AddList("uncertain", s.Uncertain);
        AddText("emotion", s.Emotion);
        AddText("location", s.Location);
        AddList("companions", s.Companions);
        AddText("physicalState", s.PhysicalState);
        AddList("goals", s.Goals);
        AddList("relationshipChanges", s.RelationshipChanges);
        AddList("secrets", s.Secrets);
        AddText("voiceNotes", s.VoiceNotes);
        AddList("inventoryChanges", s.InventoryChanges);
        return fields;
    }

    private async Task RunScanAsync()
    {
        if (_scanCts != null) return;   // already running
        _scanCts = new CancellationTokenSource();
        try
        {
            var characters = await _host.EntityService.LoadCharactersAsync().ConfigureAwait(false);
            var progress = new Progress<KnowledgeScanProgress>(p =>
                MessagePosted?.Invoke(JsonSerializer.Serialize(new
                {
                    type = "scanProgress",
                    running = true,
                    done = p.OverallDone,
                    total = p.OverallTotal,
                    sceneTitle = p.SceneTitle,
                    chapterTitle = p.ChapterTitle,
                }, Json)));

            await _extension.RunKnowledgeScanAsync(
                [.. characters], progress, _scanCts.Token).ConfigureAwait(false);

            _sceneCache = null;   // the scan wrote new records; re-read them

            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "scanProgress", running = false, done = 0, total = 0,
                sceneTitle = string.Empty, chapterTitle = string.Empty,
            }, Json));
            MessagePosted?.Invoke(await CharactersAsync().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "scanProgress", running = false, done = 0, total = 0,
                sceneTitle = string.Empty, chapterTitle = string.Empty,
            }, Json));
        }
        catch (Exception ex)
        {
            MessagePosted?.Invoke(JsonSerializer.Serialize(new
            {
                type = "scanError", message = ex.Message,
            }, Json));
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
    }
}

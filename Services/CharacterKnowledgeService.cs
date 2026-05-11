using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Extensions.AiAssistant.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

public sealed class CharacterKnowledgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IHostServices _host;
    private readonly KnowledgeBuilder _builder;
    private readonly string _extensionId;

    private readonly ConcurrentDictionary<string, CharacterKnowledgeFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public CharacterKnowledgeService(IHostServices host, KnowledgeBuilder builder, string extensionId)
    {
        _host = host;
        _builder = builder;
        _extensionId = extensionId;
    }

    private string KnowledgeRoot => _host.FileService.CombinePath(_host.GetExtensionDataPath(_extensionId), "knowledge");

    private string FilePathFor(string characterId)
        => _host.FileService.CombinePath(KnowledgeRoot, $"{characterId}.json");

    private SemaphoreSlim LockFor(string characterId)
        => _fileLocks.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Load (or create) the per-character knowledge file. Cached after first read.
    /// </summary>
    public async Task<CharacterKnowledgeFile> LoadAsync(string characterId)
    {
        if (_cache.TryGetValue(characterId, out var cached))
            return cached;

        var sem = LockFor(characterId);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(characterId, out cached))
                return cached;

            var path = FilePathFor(characterId);
            CharacterKnowledgeFile file;
            if (await _host.FileService.ExistsAsync(path).ConfigureAwait(false))
            {
                try
                {
                    var json = await _host.FileService.ReadTextAsync(path).ConfigureAwait(false);
                    file = JsonSerializer.Deserialize<CharacterKnowledgeFile>(json) ?? new CharacterKnowledgeFile { CharacterId = characterId };
                }
                catch
                {
                    file = new CharacterKnowledgeFile { CharacterId = characterId };
                }
            }
            else
            {
                file = new CharacterKnowledgeFile { CharacterId = characterId };
            }

            _cache[characterId] = file;
            return file;
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task SaveAsync(CharacterKnowledgeFile file)
    {
        var sem = LockFor(file.CharacterId);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            await _host.FileService.CreateDirectoryAsync(KnowledgeRoot).ConfigureAwait(false);
            var path = FilePathFor(file.CharacterId);
            var json = JsonSerializer.Serialize(file, JsonOptions);
            await _host.FileService.WriteTextAsync(path, json).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Mark a (character, scene) pair stale. We delete the entry; lazy regen
    /// on next access will rebuild it. Called from SceneSaved.
    /// </summary>
    public async Task InvalidateSceneAsync(string sceneId, IEnumerable<string> characterIds)
    {
        foreach (var charId in characterIds)
        {
            var file = await LoadAsync(charId).ConfigureAwait(false);
            var idx = file.Scenes.FindIndex(s => string.Equals(s.SceneId, sceneId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                file.Scenes.RemoveAt(idx);
                await SaveAsync(file).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns the knowledge entry for a (character, scene), regenerating via
    /// the LLM when missing or stale (hash mismatch). The LLM decides whether
    /// the character is meaningfully present; absence still yields a stored
    /// stub so we never reprocess the same scene+hash.
    /// </summary>
    public async Task<CharacterSceneKnowledge?> GetOrBuildAsync(
        CharacterInfo character,
        ChapterInfo chapter,
        SceneInfo scene,
        Func<Task<string>> readSceneText,
        CancellationToken cancellationToken = default)
    {
        var file = await LoadAsync(character.Id).ConfigureAwait(false);
        var sceneText = await readSceneText().ConfigureAwait(false);
        var hash = HashContent(sceneText);

        var existing = file.Scenes.FirstOrDefault(s => string.Equals(s.SceneId, scene.Id, StringComparison.OrdinalIgnoreCase));
        if (existing != null
            && string.Equals(existing.SceneContentHash, hash, StringComparison.Ordinal)
            && existing.SchemaVersion >= CharacterSceneKnowledge.CurrentSchemaVersion)
            return existing;

        // No alias prefilter — let the LLM decide whether the character is
        // actually present. The prompt instructs it to set present=false when
        // the character is only mentioned or absent.
        var built = await _builder.BuildAsync(character, sceneText, scene.Title, chapter.Title, cancellationToken).ConfigureAwait(false);
        built.SceneId = scene.Id;
        built.ChapterGuid = scene.ChapterGuid;
        built.ChapterTitle = chapter.Title;
        built.SceneTitle = scene.Title;
        built.SceneContentHash = hash;
        built.GeneratedAt = DateTime.UtcNow;

        await UpsertAsync(file, built).ConfigureAwait(false);
        return built;
    }

    private async Task UpsertAsync(CharacterKnowledgeFile file, CharacterSceneKnowledge entry)
    {
        var idx = file.Scenes.FindIndex(s => string.Equals(s.SceneId, entry.SceneId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) file.Scenes[idx] = entry;
        else file.Scenes.Add(entry);
        await SaveAsync(file).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the cumulative knowledge string for one character up to and
    /// including the given scene, in story order. Empty entries are skipped.
    /// </summary>
    public async Task<string> BuildCumulativeAsync(
        CharacterInfo character,
        string upToSceneId,
        IReadOnlyList<(ChapterInfo Chapter, SceneInfo Scene)> orderedScenes,
        Func<ChapterInfo, SceneInfo, Task<string>> readSceneText,
        IProgress<KnowledgeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int total = 0;
        for (int i = 0; i < orderedScenes.Count; i++)
        {
            total++;
            if (string.Equals(orderedScenes[i].Scene.Id, upToSceneId, StringComparison.OrdinalIgnoreCase))
                break;
        }
        if (total == 0) total = 1;

        int done = 0;
        foreach (var (chapter, scene) in orderedScenes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = await GetOrBuildAsync(
                character,
                chapter,
                scene,
                () => readSceneText(chapter, scene),
                cancellationToken).ConfigureAwait(false);

            done++;
            progress?.Report(new KnowledgeProgress
            {
                Done = done,
                Total = total,
                CharacterName = character.DisplayName,
                SceneTitle = scene.Title
            });

            if (entry != null && entry.Present && !entry.IsEmpty)
                AppendEntry(sb, entry);

            if (string.Equals(scene.Id, upToSceneId, StringComparison.OrdinalIgnoreCase))
                break;
        }

        return sb.ToString();
    }

    private static void AppendEntry(StringBuilder sb, CharacterSceneKnowledge e)
    {
        sb.Append("### ").Append(e.ChapterTitle).Append(" / ").AppendLine(e.SceneTitle);
        if (!string.IsNullOrWhiteSpace(e.Location))
            sb.Append("Location: ").AppendLine(e.Location);
        if (e.Companions.Count > 0)
            sb.Append("With: ").AppendLine(string.Join(", ", e.Companions));
        if (e.Observed.Count > 0)
        {
            sb.AppendLine("Observed:");
            foreach (var o in e.Observed) sb.Append("- ").AppendLine(o);
        }
        if (e.Learned.Count > 0)
        {
            sb.AppendLine("Learned:");
            foreach (var o in e.Learned) sb.Append("- ").AppendLine(o);
        }
        if (e.Said.Count > 0)
        {
            sb.AppendLine("Said:");
            foreach (var o in e.Said) sb.Append("- ").AppendLine(o);
        }
        if (e.Uncertain.Count > 0)
        {
            sb.AppendLine("Uncertain:");
            foreach (var o in e.Uncertain) sb.Append("- ").AppendLine(o);
        }
        if (e.Secrets.Count > 0)
        {
            sb.AppendLine("Secrets:");
            foreach (var o in e.Secrets) sb.Append("- ").AppendLine(o);
        }
        if (e.RelationshipChanges.Count > 0)
        {
            sb.AppendLine("Relationship shifts:");
            foreach (var o in e.RelationshipChanges) sb.Append("- ").AppendLine(o);
        }
        if (e.InventoryChanges.Count > 0)
        {
            sb.AppendLine("Inventory:");
            foreach (var o in e.InventoryChanges) sb.Append("- ").AppendLine(o);
        }
        if (e.Goals.Count > 0)
        {
            sb.AppendLine("Goals:");
            foreach (var o in e.Goals) sb.Append("- ").AppendLine(o);
        }
        if (!string.IsNullOrWhiteSpace(e.PhysicalState))
            sb.Append("Physical state: ").AppendLine(e.PhysicalState);
        if (!string.IsNullOrWhiteSpace(e.VoiceNotes))
            sb.Append("Voice / manner: ").AppendLine(e.VoiceNotes);
        if (!string.IsNullOrWhiteSpace(e.Emotion))
            sb.Append("Emotion: ").AppendLine(e.Emotion);
        sb.AppendLine();
    }

    /// <summary>
    /// Initial scan: for each selected character, walks every scene in story
    /// order (act → chapter → scene) and asks the LLM whether the character
    /// has anything in it. Existing entries with matching hashes are skipped.
    /// Up to <paramref name="maxParallelism"/> LLM calls run concurrently.
    /// </summary>
    public async Task ScanAsync(
        IReadOnlyList<CharacterInfo> selectedCharacters,
        IReadOnlyList<(ChapterInfo Chapter, SceneInfo Scene)> orderedScenes,
        Func<ChapterInfo, SceneInfo, Task<string>> readSceneText,
        IProgress<KnowledgeScanProgress>? progress,
        CancellationToken cancellationToken,
        int maxParallelism = 1)
    {
        // Pre-read scene texts once to avoid N round-trips per character.
        var sceneTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (chapter, scene) in orderedScenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sceneTexts[scene.Id] = await readSceneText(chapter, scene).ConfigureAwait(false);
        }

        int totalSteps = Math.Max(1, selectedCharacters.Count * orderedScenes.Count);
        int doneSteps = 0;
        int storedPresent = 0;
        int storedAbsent = 0;
        int reused = 0;

        var sessionStart = Stopwatch.StartNew();
        long llmCallTotalMs = 0;
        int llmCallCount = 0;

        // ETA timing window starts AFTER the first completed step so the
        // initial model-load latency doesn't poison the average.
        var etaStart = new Stopwatch();
        int etaDoneBaseline = -1;

        var parallelism = Math.Max(1, maxParallelism);
        var gate = new SemaphoreSlim(parallelism, parallelism);
        var fileLockMap = new ConcurrentDictionary<string, SemaphoreSlim>();
        SemaphoreSlim FileLock(string charId) => fileLockMap.GetOrAdd(charId, _ => new SemaphoreSlim(1, 1));

        // Tracks scenes currently being processed (one entry per parallel slot
        // that holds the gate). All mutations happen under sessionStart lock.
        var activeSlots = new List<ActiveSlot>(parallelism);

        void ReportNow(int charIdx, string charName, int sceneIdx, string chapterTitle, string sceneTitle)
        {
            int snapshotDone, snapshotPresent, snapshotAbsent, snapshotReused, snapshotEtaBaseline;
            long snapshotLlmMs;
            int snapshotLlmCount;
            ActiveSlot[] slotsSnapshot;
            TimeSpan snapshotEtaElapsed;
            lock (sessionStart)
            {
                snapshotDone = doneSteps;
                snapshotPresent = storedPresent;
                snapshotAbsent = storedAbsent;
                snapshotReused = reused;
                snapshotLlmMs = llmCallTotalMs;
                snapshotLlmCount = llmCallCount;
                slotsSnapshot = activeSlots.ToArray();
                snapshotEtaBaseline = etaDoneBaseline;
                snapshotEtaElapsed = etaStart.Elapsed;
            }
            progress?.Report(BuildProgress(
                snapshotDone, totalSteps, charIdx, selectedCharacters.Count, charName,
                sceneIdx, orderedScenes.Count, sceneTitle, chapterTitle,
                snapshotPresent, snapshotAbsent, snapshotReused,
                snapshotLlmMs, snapshotLlmCount, sessionStart.Elapsed,
                slotsSnapshot, snapshotEtaElapsed, snapshotEtaBaseline));
        }

        for (int ci = 0; ci < selectedCharacters.Count; ci++)
        {
            var character = selectedCharacters[ci];
            var charIndex = ci + 1;
            var file = await LoadAsync(character.Id).ConfigureAwait(false);

            var sceneTasks = new List<Task>(orderedScenes.Count);
            for (int si = 0; si < orderedScenes.Count; si++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sceneIndex = si + 1;
                var (chapter, scene) = orderedScenes[si];

                sceneTasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var slot = new ActiveSlot
                    {
                        CharacterName = character.DisplayName,
                        ChapterTitle = chapter.Title,
                        SceneTitle = scene.Title
                    };
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var sceneText = sceneTexts.TryGetValue(scene.Id, out var t) ? t : string.Empty;
                        var hash = HashContent(sceneText);

                        CharacterSceneKnowledge? existing;
                        var fileLock = FileLock(character.Id);
                        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            existing = file.Scenes.FirstOrDefault(s => string.Equals(s.SceneId, scene.Id, StringComparison.OrdinalIgnoreCase));
                        }
                        finally { fileLock.Release(); }

                        if (existing != null
                            && string.Equals(existing.SceneContentHash, hash, StringComparison.Ordinal)
                            && existing.SchemaVersion >= CharacterSceneKnowledge.CurrentSchemaVersion)
                        {
                            lock (sessionStart)
                            {
                                reused++;
                                doneSteps++;
                                if (etaDoneBaseline < 0) { etaDoneBaseline = doneSteps; etaStart.Restart(); }
                            }
                            ReportNow(charIndex, character.DisplayName, sceneIndex, chapter.Title, scene.Title);
                            return;
                        }

                        // Mark slot active before issuing LLM call.
                        lock (sessionStart) { activeSlots.Add(slot); }
                        ReportNow(charIndex, character.DisplayName, sceneIndex, chapter.Title, scene.Title);

                        var sw = Stopwatch.StartNew();
                        var built = await _builder.BuildAsync(character, sceneText, scene.Title, chapter.Title, cancellationToken).ConfigureAwait(false);
                        sw.Stop();

                        built.SceneId = scene.Id;
                        built.ChapterGuid = scene.ChapterGuid;
                        built.ChapterTitle = chapter.Title;
                        built.SceneTitle = scene.Title;
                        built.SceneContentHash = hash;
                        built.GeneratedAt = DateTime.UtcNow;

                        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await UpsertAsync(file, built).ConfigureAwait(false);
                        }
                        finally { fileLock.Release(); }

                        lock (sessionStart)
                        {
                            llmCallTotalMs += sw.ElapsedMilliseconds;
                            llmCallCount++;
                            doneSteps++;
                            if (built.Present) storedPresent++;
                            else storedAbsent++;
                            activeSlots.Remove(slot);
                            if (etaDoneBaseline < 0) { etaDoneBaseline = doneSteps; etaStart.Restart(); }
                        }

                        ReportNow(charIndex, character.DisplayName, sceneIndex, chapter.Title, scene.Title);
                    }
                    catch
                    {
                        lock (sessionStart) { activeSlots.Remove(slot); }
                        throw;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(sceneTasks).ConfigureAwait(false);
        }

        // Final report so the UI lands on 100%.
        progress?.Report(BuildProgress(
            totalSteps, totalSteps, selectedCharacters.Count, selectedCharacters.Count, string.Empty,
            orderedScenes.Count, orderedScenes.Count, string.Empty, string.Empty,
            storedPresent, storedAbsent, reused,
            llmCallTotalMs, llmCallCount, sessionStart.Elapsed,
            Array.Empty<ActiveSlot>(), etaStart.Elapsed, etaDoneBaseline));
    }

    private static KnowledgeScanProgress BuildProgress(
        int doneSteps, int totalSteps,
        int charIndex, int charTotal, string charName,
        int sceneIndex, int sceneTotal, string sceneTitle, string chapterTitle,
        int storedPresent, int storedAbsent, int reused,
        long llmTotalMs, int llmCount, TimeSpan sessionElapsed,
        IReadOnlyList<ActiveSlot> activeSlots,
        TimeSpan etaWindowElapsed, int etaDoneBaseline)
    {
        // ETA strategy: extrapolate from elapsed-per-done within a window that
        // EXCLUDES the first completed step (which absorbs initial model-load
        // latency). Falls back to "—" until at least one step is done.
        long etaMs = 0;
        long avgMs = 0;
        if (etaDoneBaseline >= 0)
        {
            int etaDone = Math.Max(0, doneSteps - etaDoneBaseline);
            if (etaDone > 0)
            {
                avgMs = (long)(etaWindowElapsed.TotalMilliseconds / etaDone);
                etaMs = avgMs * Math.Max(0, totalSteps - doneSteps);
            }
        }

        return new KnowledgeScanProgress
        {
            OverallDone = doneSteps,
            OverallTotal = totalSteps,
            CharacterIndex = charIndex,
            CharacterTotal = charTotal,
            CharacterName = charName,
            SceneIndex = sceneIndex,
            SceneTotal = sceneTotal,
            SceneTitle = sceneTitle,
            ChapterTitle = chapterTitle,
            StoredPresent = storedPresent,
            StoredAbsent = storedAbsent,
            Reused = reused,
            AverageStepMs = avgMs,
            EstimatedRemainingMs = etaMs,
            ElapsedMs = (long)sessionElapsed.TotalMilliseconds,
            ActiveSlots = activeSlots
        };
    }

    /// <summary>
    /// Wipes every per-character knowledge file and clears the in-memory cache.
    /// Use when the user wants to start fresh.
    /// </summary>
    public async Task ClearCacheAsync()
    {
        _cache.Clear();
        var dir = KnowledgeRoot;
        if (!await _host.FileService.DirectoryExistsAsync(dir).ConfigureAwait(false))
            return;

        var files = await _host.FileService.GetFilesAsync(dir, "*.json").ConfigureAwait(false);
        foreach (var f in files)
        {
            try
            {
                // IExtensionFileService has no delete; overwrite with empty file then leave it.
                // Better: we DO have a host file service that can write — but no delete API.
                // The safest portable behaviour is to overwrite with an empty knowledge file.
                var empty = JsonSerializer.Serialize(new CharacterKnowledgeFile
                {
                    CharacterId = _host.FileService.GetFileNameWithoutExtension(f)
                }, JsonOptions);
                await _host.FileService.WriteTextAsync(f, empty).ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>
    /// Returns the subset of provided characters that are actually mentioned
    /// in the scene text (by name, surname, or alias).
    /// </summary>
    public static List<string> DetectPresentCharacterIds(IReadOnlyList<CharacterInfo> characters, string sceneText)
    {
        var ids = new List<string>();
        foreach (var c in characters)
        {
            if (IsCharacterPresent(c, sceneText))
                ids.Add(c.Id);
        }
        return ids;
    }

    private static bool IsCharacterPresent(CharacterInfo character, string sceneText)
    {
        if (string.IsNullOrWhiteSpace(sceneText)) return false;
        foreach (var alias in EnumerateAliases(character))
        {
            if (string.IsNullOrWhiteSpace(alias)) continue;
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(alias)}(?![\p{{L}}\p{{N}}])";
            if (Regex.IsMatch(sceneText, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateAliases(CharacterInfo character)
    {
        if (!string.IsNullOrWhiteSpace(character.DisplayName)) yield return character.DisplayName;
        foreach (var a in character.Aliases) yield return a;
    }

    private static string HashContent(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        var hash = SHA1.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

public sealed class KnowledgeProgress
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public string SceneTitle { get; init; } = string.Empty;
    public double Fraction => Total == 0 ? 0 : (double)Done / Total;
}

/// <summary>Detailed progress for the initial scan UI.</summary>
public sealed class KnowledgeScanProgress
{
    public int OverallDone { get; init; }
    public int OverallTotal { get; init; }
    public int CharacterIndex { get; init; }
    public int CharacterTotal { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public int SceneIndex { get; init; }
    public int SceneTotal { get; init; }
    public string SceneTitle { get; init; } = string.Empty;
    public string ChapterTitle { get; init; } = string.Empty;
    public int StoredPresent { get; init; }
    public int StoredAbsent { get; init; }
    public int Reused { get; init; }
    public long AverageStepMs { get; init; }
    public long EstimatedRemainingMs { get; init; }
    public long ElapsedMs { get; init; }

    /// <summary>Snapshot of currently-running LLM calls (one per parallel slot).</summary>
    public IReadOnlyList<ActiveSlot> ActiveSlots { get; init; } = Array.Empty<ActiveSlot>();

    public double OverallFraction => OverallTotal == 0 ? 0 : (double)OverallDone / OverallTotal;
}

public sealed class ActiveSlot
{
    public string CharacterName { get; init; } = string.Empty;
    public string ChapterTitle { get; init; } = string.Empty;
    public string SceneTitle { get; init; } = string.Empty;
}

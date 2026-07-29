using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>One proposed scene.</summary>
public sealed record OutlineScene(string Title, string Synopsis, string Pov, string Plotline);

/// <summary>One proposed chapter.</summary>
public sealed record OutlineChapter(
    string Title, string Act, IReadOnlyList<OutlineScene> Scenes);

/// <summary>A proposed shape for a book.</summary>
public sealed record Outline(
    IReadOnlyList<OutlineChapter> Chapters,
    IReadOnlyList<string> Plotlines,
    string? Error);

/// <summary>What materialising an outline actually did.</summary>
public sealed record OutlineResult(int Chapters, int Scenes, int Plotlines);

/// <summary>
/// Turns a premise into a shape: chapters, scenes, synopses and plot threads.
///
/// The important word is shape. This writes no prose. What it produces is a binder
/// full of titled, empty scenes with a line of synopsis each - which is exactly
/// what an outline is, and exactly what a writer can then argue with, reorder and
/// throw half of away.
///
/// It materialises into the project rather than printing a plan to be copied out.
/// That is the whole difference between this and pasting a premise into a chat
/// window, and it is only possible because the SDK gained structural editing.
/// Nothing is overwritten: chapters are appended, so running this on a book that
/// already has chapters adds to it rather than replacing what is there.
/// </summary>
public sealed class OutlineService
{
    private readonly AiService _ai;
    private readonly IHostServices _host;
    private readonly IExtensionLocalization _loc;

    public OutlineService(AiService ai, IHostServices host, IExtensionLocalization loc)
    {
        _ai = ai;
        _host = host;
        _loc = loc;
    }

    /// <summary>
    /// Asks for an outline.
    /// </summary>
    /// <param name="premise">What the book is about, in the writer's words.</param>
    /// <param name="chapters">Roughly how many chapters to aim for.</param>
    /// <param name="structure">
    /// A named structure to follow ("three-act", "save-the-cat"), or empty to let
    /// the shape follow the premise.
    /// </param>
    public async Task<Outline> ProposeAsync(
        string premise,
        int chapters,
        string structure = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(premise))
            return new Outline([], [], _loc.T("outline.needsPremise"));

        var user = new StringBuilder();
        user.AppendLine("PREMISE:").AppendLine(premise.Trim()).AppendLine();
        user.AppendLine($"TARGET LENGTH: about {Math.Clamp(chapters, 3, 80)} chapters.");
        if (!string.IsNullOrWhiteSpace(structure))
            user.AppendLine($"STRUCTURE TO FOLLOW: {structure.Trim()}");

        // What the writer already has, so the outline uses their cast rather than
        // inventing a parallel one.
        var cast = await CastAsync().ConfigureAwait(false);
        if (cast.Length > 0)
            user.AppendLine().AppendLine("CHARACTERS ALREADY IN THE CODEX:").AppendLine(cast);

        var messages = new List<SdkAiChatMessage>
        {
            new() { Role = "system", Content = SystemPrompt() },
            new() { Role = "user", Content = user.ToString() },
        };

        try
        {
            var result = await _ai.GenerateChatAsync(
                messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(result.Response ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new Outline([], [], null);
        }
        catch (Exception ex)
        {
            return new Outline([], [], ex.Message);
        }
    }

    /// <summary>
    /// Builds the outline into the project: plot threads first, then chapters,
    /// then scenes with their synopses and threads.
    ///
    /// Threads are created before the scenes that belong to them, because a scene
    /// cannot be put on a thread that does not exist yet - and doing it the other
    /// way round means a second pass over every scene.
    /// </summary>
    public async Task<OutlineResult> MaterialiseAsync(
        Outline outline, CancellationToken cancellationToken = default)
    {
        var threadIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existing = _host.StoryService.GetPlotlines();

        foreach (var name in outline.Plotlines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // A thread the writer already has is reused rather than duplicated.
            var already = existing.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            threadIds[name] = already != null
                ? already.Id
                : await _host.StoryService.CreatePlotlineAsync(name).ConfigureAwait(false);
        }

        var chapterCount = 0;
        var sceneCount = 0;

        foreach (var chapter in outline.Chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chapterGuid = await _host.ProjectService
                .CreateChapterAsync(chapter.Title).ConfigureAwait(false);
            if (string.IsNullOrEmpty(chapterGuid)) continue;
            chapterCount++;

            if (!string.IsNullOrWhiteSpace(chapter.Act))
                await _host.ProjectService
                    .SetChapterActAsync(chapterGuid, chapter.Act).ConfigureAwait(false);

            foreach (var scene in chapter.Scenes)
            {
                var sceneId = await _host.ProjectService
                    .CreateSceneAsync(chapterGuid, scene.Title).ConfigureAwait(false);
                if (string.IsNullOrEmpty(sceneId)) continue;
                sceneCount++;

                // The synopsis is the outline. A titled empty scene with no line
                // of intent behind it tells the writer nothing they did not
                // already know from the chapter list.
                if (!string.IsNullOrWhiteSpace(scene.Synopsis))
                    await _host.ProjectService
                        .SetSceneSynopsisAsync(chapterGuid, sceneId, scene.Synopsis)
                        .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(scene.Plotline)
                    && threadIds.TryGetValue(scene.Plotline, out var threadId)
                    && !string.IsNullOrEmpty(threadId))
                {
                    await _host.StoryService
                        .SetScenePlotlinesAsync(chapterGuid, sceneId, [threadId])
                        .ConfigureAwait(false);
                }
            }
        }

        return new OutlineResult(chapterCount, sceneCount, threadIds.Count);
    }

    private string SystemPrompt() =>
        "You outline novels. Given a premise, propose a chapter-and-scene structure.\n\n"
        + "Reply as a JSON object and nothing else:\n"
        + "{\"plotlines\": [\"short name of each thread running through the book\"],\n"
        + " \"chapters\": [{\"title\": \"chapter title\", \"act\": \"which act, or empty\",\n"
        + "   \"scenes\": [{\"title\": \"scene title\", \"synopsis\": \"one sentence saying what "
        + "happens and what changes\", \"pov\": \"whose point of view\", "
        + "\"plotline\": \"which thread from the list above\"}]}]}\n\n"
        + "What makes this useful rather than decorative:\n"
        + "- Every scene's synopsis says what changes in it. A scene where nothing changes is not "
        + "a scene, it is a description.\n"
        + "- Two to five scenes a chapter. A chapter of one scene is a scene; a chapter of ten is "
        + "an act.\n"
        + "- Use the characters supplied if any were. Do not invent a second protagonist alongside "
        + "one the author already has.\n"
        + "- Name threads for what they are about, not \"Subplot A\".\n"
        + "- Write no prose. This is a shape for the author to argue with.\n\n"
        + $"Write the titles and synopses in {_ai.LanguageName}.";

    internal static Outline Parse(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start) return new Outline([], [], "The outline could not be read.");

        try
        {
            using var document = JsonDocument.Parse(response[start..(end + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new Outline([], [], "The outline could not be read.");

            var plotlines = Strings(root, "plotlines");
            var chapters = new List<OutlineChapter>();

            if (root.TryGetProperty("chapters", out var chapterArray)
                && chapterArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in chapterArray.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    var title = Text(element, "title")?.Trim();
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    chapters.Add(new OutlineChapter(
                        title!, Text(element, "act")?.Trim() ?? string.Empty, Scenes(element)));
                }
            }

            return chapters.Count == 0
                ? new Outline([], plotlines, "The outline had no chapters in it.")
                : new Outline(chapters, plotlines, null);
        }
        catch (JsonException)
        {
            return new Outline([], [], "The outline could not be read.");
        }
    }

    private static IReadOnlyList<OutlineScene> Scenes(JsonElement chapter)
    {
        if (!chapter.TryGetProperty("scenes", out var value)
            || value.ValueKind != JsonValueKind.Array) return [];

        var scenes = new List<OutlineScene>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var title = Text(element, "title")?.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            scenes.Add(new OutlineScene(
                title!,
                Text(element, "synopsis")?.Trim() ?? string.Empty,
                Text(element, "pov")?.Trim() ?? string.Empty,
                Text(element, "plotline")?.Trim() ?? string.Empty));
        }
        return scenes;
    }

    private async Task<string> CastAsync()
    {
        var builder = new StringBuilder();
        foreach (var character in await _host.EntityService.LoadCharactersAsync().ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(character.DisplayName)) continue;
            builder.Append("- ").Append(character.DisplayName);
            if (!string.IsNullOrWhiteSpace(character.Role))
                builder.Append(" - ").Append(character.Role);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];
}

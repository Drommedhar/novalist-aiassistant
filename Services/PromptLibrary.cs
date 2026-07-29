using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>One prompt the writer wrote.</summary>
public sealed class SavedPrompt
{
    /// <summary>Stable id. Also the suffix of the inline action's id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What the menu entry says.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The instruction. Sent as the system message.</summary>
    public string System { get; set; } = string.Empty;

    /// <summary>
    /// The message body, with placeholders. Empty falls back to the selection,
    /// which is what nearly every prompt wants.
    /// </summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// What to do with the answer: "replace", "after" or "caret".
    /// </summary>
    public string Disposition { get; set; } = "after";

    /// <summary>Whether the action appears with nothing selected.</summary>
    public bool AllowsEmptySelection { get; set; }

    /// <summary>Slash-menu keyword, without the slash. Empty means none.</summary>
    public string SlashKeyword { get; set; } = string.Empty;

    /// <summary>How many wordings to ask for. Two or more shows a picker.</summary>
    public int Candidates { get; set; } = 1;
}

/// <summary>What a template can be given.</summary>
public sealed record PromptInputs(
    string Selection,
    string PrecedingText,
    string Directive,
    string SceneTitle,
    string ChapterTitle,
    string Synopsis,
    string Pov,
    string Characters,
    string Context);

/// <summary>
/// The writer's own prompts, with a small template language.
///
/// Every AI feature in here is somebody's opinion about what a writer needs said
/// to a model. That opinion is often wrong for a particular book: a prompt tuned
/// for a thriller is not the one a literary novelist wants, and neither is the
/// one for a translator working into German. So the prompts are editable, and the
/// writer's own sit in the same menu as the built-in ones.
///
/// The template language is deliberately tiny - substitution and nothing else. No
/// conditionals, no loops, no expressions. A prompt is a piece of writing, and
/// giving it a programming language means debugging it, which is not what
/// somebody opened a novel-writing application to do.
/// </summary>
public static partial class PromptLibrary
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// The placeholders a template may use, with what each one is for. Shown in
    /// the editor, so the list is documentation rather than something to look up.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Means)> Placeholders =
    [
        ("selection", "The highlighted text."),
        ("preceding", "The prose before the caret."),
        ("directive", "What you typed after the slash."),
        ("scene", "The scene's title."),
        ("chapter", "The chapter's title."),
        ("synopsis", "The scene's synopsis."),
        ("pov", "Whose point of view the scene is in."),
        ("characters", "The cast, one per line."),
        ("context", "Codex entries this scene may send to a model.")
    ];

    /// <summary>
    /// Fills a template in.
    ///
    /// A placeholder that is not one of the known names is left exactly as typed.
    /// A writer who wrote {{tone}} meant something by it, and eating it silently
    /// would leave them wondering where their words went - whereas seeing it come
    /// back tells them at once that it is not a placeholder.
    /// </summary>
    public static string Fill(string template, PromptInputs inputs)
        => PlaceholderRegex().Replace(template ?? string.Empty, match =>
        {
            var value = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "selection" => inputs.Selection,
                "preceding" => inputs.PrecedingText,
                "directive" => inputs.Directive,
                "scene" => inputs.SceneTitle,
                "chapter" => inputs.ChapterTitle,
                "synopsis" => inputs.Synopsis,
                "pov" => inputs.Pov,
                "characters" => inputs.Characters,
                "context" => inputs.Context,
                _ => null
            };
            return value ?? match.Value;
        });

    /// <summary>Which placeholders a template uses. Lets the caller gather only what is needed.</summary>
    public static IReadOnlyList<string> Uses(string template)
        => [.. PlaceholderRegex().Matches(template ?? string.Empty)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Cleans a list on the way in. A prompt with no label or no instruction has
    /// nothing to show and nothing to say, so it is dropped rather than appearing
    /// as a blank menu entry that does nothing.
    /// </summary>
    public static IReadOnlyList<SavedPrompt> Clean(IEnumerable<SavedPrompt>? prompts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<SavedPrompt>();

        foreach (var prompt in prompts ?? [])
        {
            var label = (prompt.Label ?? string.Empty).Trim();
            var system = (prompt.System ?? string.Empty).Trim();
            if (label.Length == 0 || system.Length == 0) continue;

            var id = (prompt.Id ?? string.Empty).Trim();
            if (id.Length == 0) id = Slug(label);
            if (id.Length == 0 || !seen.Add(id)) continue;

            cleaned.Add(new SavedPrompt
            {
                Id = id,
                Label = label,
                System = system,
                Template = (prompt.Template ?? string.Empty).Trim(),
                Disposition = prompt.Disposition?.ToLowerInvariant() switch
                {
                    "replace" => "replace",
                    "caret" => "caret",
                    _ => "after"
                },
                AllowsEmptySelection = prompt.AllowsEmptySelection,
                SlashKeyword = Slug(prompt.SlashKeyword ?? string.Empty),
                Candidates = Math.Clamp(prompt.Candidates, 1, 5)
            });
        }

        return cleaned;
    }

    internal static string Slug(string text)
        => Regex.Replace((text ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public static string Serialise(IReadOnlyList<SavedPrompt> prompts)
        => JsonSerializer.Serialize(prompts, JsonOptions);

    /// <summary>
    /// Reads the library. A file that will not parse yields no prompts rather
    /// than throwing: a broken prompt file should cost the writer their custom
    /// prompts, not their editor.
    /// </summary>
    public static IReadOnlyList<SavedPrompt> Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return Clean(JsonSerializer.Deserialize<List<SavedPrompt>>(json, JsonOptions));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A few prompts to start from, so the editor is not an empty box.
    ///
    /// These are examples of the shape rather than a curated set - the point is
    /// that a writer reads one, sees how it works, and writes their own.
    /// </summary>
    public static IReadOnlyList<SavedPrompt> Examples() =>
    [
        new SavedPrompt
        {
            Id = "harder",
            Label = "Make this harder on them",
            System =
                "You are a novelist's collaborator. The author gives you a passage in which "
                + "something goes too easily for a character. Rewrite it so the same outcome "
                + "costs them more - effort, a wound, a compromise, or time. Do not change what "
                + "happens, only what it costs. Preserve point of view, tense and voice. Output "
                + "only the rewritten passage.",
            Template = "PASSAGE:\n{{selection}}",
            Disposition = "replace",
            Candidates = 2
        },
        new SavedPrompt
        {
            Id = "in-their-voice",
            Label = "Say it in their voice",
            System =
                "You rewrite dialogue so it sounds like the character speaking rather than the "
                + "author. Use the character notes supplied to find their vocabulary, rhythm and "
                + "evasions. Keep the meaning and the beats. Output only the rewritten dialogue.",
            Template = "WHO IS SPEAKING: {{pov}}\n\nWHAT IS KNOWN ABOUT THEM:\n{{context}}\n\nDIALOGUE:\n{{selection}}",
            Disposition = "replace",
            Candidates = 3
        },
        new SavedPrompt
        {
            Id = "what-is-missing",
            Label = "What is this scene missing?",
            System =
                "You are a developmental editor. Read the scene and say what it is missing in "
                + "three short bullets - a beat that is skipped, a sense that is absent, a "
                + "question the reader will have. Do not rewrite anything and do not praise it.",
            Template = "SCENE: {{scene}}\nWHAT IT IS FOR: {{synopsis}}\n\nPROSE:\n{{selection}}",
            Disposition = "after",
            AllowsEmptySelection = true,
            SlashKeyword = "missing"
        }
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

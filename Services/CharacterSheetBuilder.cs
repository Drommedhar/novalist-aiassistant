using System.Text;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Renders a character's resolved profile as a Markdown "CHARACTER SHEET" block.
/// Used by the character-chat system prompt and by knowledge extraction so both
/// flows see identical traits, relationships, and scope-resolved overrides.
/// </summary>
public static class CharacterSheetBuilder
{
    /// <summary>
    /// Builds the full sheet from a scope-resolved <see cref="CharacterDetailedInfo"/>.
    /// Caller is responsible for the heading (Build does NOT emit "# CHARACTER SHEET"
    /// itself so callers can pick their heading level).
    /// </summary>
    public static string Build(CharacterDetailedInfo detailed)
    {
        var sb = new StringBuilder();
        AppendBody(sb, detailed);
        return sb.ToString();
    }

    /// <summary>
    /// Lightweight fallback when only <see cref="CharacterInfo"/> is available
    /// (e.g. the host could not resolve the detailed view).
    /// </summary>
    public static string BuildFallback(CharacterInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {info.DisplayName}");
        if (!string.IsNullOrWhiteSpace(info.Role))
            sb.AppendLine($"Role: {info.Role}");
        if (info.Aliases is { Count: > 0 })
            sb.AppendLine($"Aliases: {string.Join(", ", info.Aliases)}");
        return sb.ToString();
    }

    private static void AppendBody(StringBuilder sb, CharacterDetailedInfo detailed)
    {
        sb.AppendLine($"Name: {detailed.DisplayName}");

        if (detailed.Aliases is { Count: > 0 })
            sb.AppendLine($"Aliases: {string.Join(", ", detailed.Aliases)}");

        Field(sb, "Age", detailed.Age);
        Field(sb, "Gender", detailed.Gender);
        Field(sb, "Role", detailed.Role);
        Field(sb, "Group / faction", detailed.Group);
        Field(sb, "Eye color", detailed.EyeColor);
        Field(sb, "Hair color", detailed.HairColor);
        Field(sb, "Hair length", detailed.HairLength);
        Field(sb, "Height", detailed.Height);
        Field(sb, "Build", detailed.Build);
        Field(sb, "Skin tone", detailed.SkinTone);
        Field(sb, "Distinguishing features", detailed.DistinguishingFeatures);

        if (detailed.CustomProperties is { Count: > 0 })
        {
            var any = false;
            foreach (var kv in detailed.CustomProperties)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (!any) { sb.AppendLine(); sb.AppendLine("Additional traits:"); any = true; }
                sb.AppendLine($"- {kv.Key}: {kv.Value}");
            }
        }

        if (detailed.Relationships is { Count: > 0 })
        {
            var any = false;
            foreach (var r in detailed.Relationships)
            {
                if (string.IsNullOrWhiteSpace(r.TargetName)) continue;
                if (!any) { sb.AppendLine(); sb.AppendLine("Relationships:"); any = true; }
                var role = string.IsNullOrWhiteSpace(r.Role) ? "(unspecified)" : r.Role;
                var note = string.IsNullOrWhiteSpace(r.Note) ? string.Empty : $" — {r.Note}";
                sb.AppendLine($"- {role}: {r.TargetName}{note}");
            }
        }

        if (detailed.Sections is { Count: > 0 })
        {
            var any = false;
            foreach (var s in detailed.Sections)
            {
                if (string.IsNullOrWhiteSpace(s.Content)) continue;
                if (!any) { sb.AppendLine(); sb.AppendLine("Profile sections:"); any = true; }
                sb.AppendLine();
                sb.AppendLine($"## {s.Title}");
                sb.AppendLine(s.Content);
            }
        }

        if (!string.IsNullOrWhiteSpace(detailed.ResolvedFromScope))
        {
            sb.AppendLine();
            sb.AppendLine($"(The above profile has scene/chapter/act overrides applied for scope: {detailed.ResolvedFromScope}.)");
        }
    }

    private static void Field(StringBuilder sb, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"{label}: {value}");
    }
}

using Novalist.Sdk.Models.Wizards;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Asks what to do with character knowledge generated before the shared
/// scene-record pipeline existed.
///
/// The old data was produced one character at a time, with each character judged
/// against the scene in isolation. It is not wrong, but it does not agree with a
/// scene record: two characters in the same scene could disagree about who was
/// there, and nothing ties an entry back to a Codex id. Rather than silently
/// keeping data of a different provenance or silently deleting the user's
/// generated work, the choice is theirs.
/// </summary>
public static class KnowledgeMigrationWizard
{
    /// <summary>Answer id for keeping the old entries as a fallback.</summary>
    public const string KeepValue = "keep";

    /// <summary>Answer id for discarding them and re-running.</summary>
    public const string ClearValue = "clear";

    /// <summary>The step id the choice is stored under.</summary>
    public const string StepId = "legacyKnowledge";

    public static WizardDefinition Build(Func<string, string>? loc, int characterFiles)
    {
        string T(string key, string fallback)
            => loc?.Invoke(key) is { } v && v != key ? v : fallback;

        return new WizardDefinition
        {
            Id = "ai.knowledgeMigration",
            // The count belongs in the translated sentence, not spliced onto the
            // end of it - German puts it somewhere else.
            Description = T("wizard.knowledgeMigration.description",
                    "Novalist now analyses each scene once and derives every character's knowledge "
                    + "from that single record. You have knowledge for {0} character(s) "
                    + "from the previous approach, which was generated per character instead.")
                .Replace("{0}", characterFiles.ToString()),
            Scope = WizardScope.Project,
            Steps =
            [
                new ChoiceStep
                {
                    Id = StepId,
                    Title = T("wizard.knowledgeMigration.choice.title", "What should happen to it?"),
                    Help = T("wizard.knowledgeMigration.choice.help",
                        "Keeping it costs nothing now but mixes two kinds of data. Clearing it means "
                        + "a re-run, which is one pass per scene rather than per character."),
                    Skippable = false,
                    Choices =
                    [
                        new WizardChoice
                        {
                            Value = KeepValue,
                            Label = T("wizard.knowledgeMigration.keep", "Keep it as a fallback"),
                            Description = T("wizard.knowledgeMigration.keepDesc",
                                "Old entries stay and are used where no scene record exists yet. "
                                + "They are replaced as scenes get analysed."),
                        },
                        new WizardChoice
                        {
                            Value = ClearValue,
                            Label = T("wizard.knowledgeMigration.clear", "Clear it and re-run"),
                            Description = T("wizard.knowledgeMigration.clearDesc",
                                "Discards the old entries. Character knowledge stays empty until "
                                + "you run the scan again."),
                        },
                    ],
                },
            ],
        };
    }
}

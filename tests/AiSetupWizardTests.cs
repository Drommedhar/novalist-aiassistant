using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Sdk.Models;
using Novalist.Sdk.Models.Wizards;
using Xunit;

public class AiSetupWizardTests
{
    [Theory]
    [InlineData("ai", true, false)]
    [InlineData("dictation", false, true)]
    [InlineData("ai,dictation", true, true)]
    [InlineData("none", false, false)]
    public void FeatureChoicePersistsAcrossRestarts(string features, bool ai, bool dictation)
    {
        var settings = new AiSettings { Enabled = true, DictationEnabled = true, LmStudioModel = "saved-model" };
        var answers = AiSetupWizard.CreateSeed(settings);
        answers.Completed = true;
        answers.Answers["features"] = new() { Text = features };
        answers.Answers["dictationModel"] = new() { Text = "large-v3" };
        answers.Answers["dictationAcceleration"] = new() { Text = "cpu" };
        Assert.True(AiSetupWizard.Apply(settings, answers));
        var loaded = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(ai, loaded.Enabled);
        Assert.Equal(dictation, loaded.DictationEnabled);
        Assert.True(loaded.SetupCompleted);
        Assert.False(AiSetupWizard.ShouldOffer(loaded));
        Assert.Equal(features, AiSetupWizard.CreateSeed(loaded).GetText("features"));
        Assert.Equal("saved-model", loaded.LmStudioModel);
        Assert.Equal(dictation ? "large-v3" : "small", loaded.DictationModel);
    }

    [Fact]
    public void FreshInstallPromptsButAnExistingDictationChoiceDoesNotDependOnModelReadiness()
    {
        Assert.True(AiSetupWizard.ShouldOffer(new AiSettings()));
        Assert.False(AiSetupWizard.ShouldOffer(new AiSettings(), hasSavedDictationChoice: true));
        Assert.False(AiSetupWizard.ShouldOffer(new AiSettings { Enabled = true }));
        Assert.False(AiSetupWizard.ShouldOffer(new AiSettings { SetupCompleted = true, DictationEnabled = false }));
    }

    [Fact]
    public void CancellingDoesNotChangeEitherFeatureOrMarkSetupComplete()
    {
        var settings = new AiSettings();
        var before = JsonSerializer.Serialize(settings);
        var answers = AiSetupWizard.CreateSeed(settings);
        answers.Answers["features"] = new() { Text = "none" };
        Assert.False(AiSetupWizard.Apply(settings, answers));
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Theory]
    [InlineData("ai", true, false)]
    [InlineData("dictation", false, true)]
    [InlineData("ai,dictation", true, true)]
    [InlineData("none", false, false)]
    public void HiddenSeededProviderCannotExposeAiStepsForDictationOnly(string features, bool ai, bool dictation)
    {
        var wizard = AiSetupWizard.Build();
        var answers = AiSetupWizard.CreateSeed(new AiSettings());
        answers.Answers["features"] = new() { Text = features };
        bool Visible(string id)
        {
            var condition = wizard.Steps.Single(s => s.Id == id).VisibleWhen;
            if (condition == null) return true;
            return Visible(condition.StepId) && (condition.Operator == "contains"
                ? answers.GetText(condition.StepId).Contains(condition.Value!)
                : answers.GetText(condition.StepId) == condition.Value);
        }
        Assert.Equal(ai, Visible("provider"));
        Assert.Equal(ai, Visible("lmStudioBaseUrl"));
        Assert.Equal(ai, Visible("responseLanguage"));
        Assert.Equal(dictation, Visible("dictationModel"));
        Assert.Equal(dictation, Visible("dictationDialogueModel"));
        Assert.Equal(dictation, Visible("dictationAcceleration"));
        Assert.Equal(dictation, Visible("prepareDictation"));
    }
}

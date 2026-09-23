using AFClaude;

namespace AFClaude.Tests;

public class FoundryRejectionLearnerTests
{
    [Fact]
    public void NothingLearned_SendsEverything()
    {
        var learner = new FoundryRejectionLearner();

        Assert.Null(learner.AcceptedToolTypes);
        Assert.False(learner.IsRejectedField("context_management"));
    }

    [Fact]
    public void ToolTagRejection_AdoptsFoundrysAcceptedList()
    {
        var learner = new FoundryRejectionLearner();

        Assert.True(learner.TryLearn(
            "tools.190: Input tag 'advisor_20260301' found using 'type' does not match any of the expected tags: 'bash_20250124', 'web_search_20250305'"));

        Assert.Equal(["bash_20250124", "custom", "web_search_20250305"], learner.AcceptedToolTypes!.Order());
    }

    [Fact]
    public void ToolTagRejection_WithoutParseableList_FallsBackToKnownTypes()
    {
        var learner = new FoundryRejectionLearner();

        Assert.True(learner.TryLearn("tools.0: Input tag 'advisor_20260301' found using 'type' does not match any of the expected tags:"));

        Assert.Same(FoundryRejectionLearner.KnownToolTypes, learner.AcceptedToolTypes);
    }

    [Fact]
    public void SameRejectionTwice_IsNotNew()
    {
        // A retry that hits the identical error means stripping didn't help -- the
        // caller must surface it rather than loop.
        const string error = "tools.1: Input tag 'x_1' found using 'type' does not match any of the expected tags: 'custom', 'bash_20250124'";
        var learner = new FoundryRejectionLearner();

        Assert.True(learner.TryLearn(error));
        Assert.False(learner.TryLearn(error));
    }

    [Fact]
    public void ExtraInputs_LearnsTopLevelFieldsOnly()
    {
        var learner = new FoundryRejectionLearner();

        Assert.True(learner.TryLearn(
            "context_management: Extra inputs are not permitted; tools.3.cache_control: Extra inputs are not permitted"));

        Assert.True(learner.IsRejectedField("context_management"));
        Assert.False(learner.IsRejectedField("cache_control"));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("messages")]
    [InlineData("max_tokens")]
    public void ExtraInputs_NeverLearnsRequiredFields(string field)
    {
        var learner = new FoundryRejectionLearner();

        Assert.False(learner.TryLearn($"{field}: Extra inputs are not permitted"));
        Assert.False(learner.IsRejectedField(field));
    }

    [Fact]
    public void UnrelatedError_LearnsNothing()
    {
        Assert.False(new FoundryRejectionLearner().TryLearn("messages: at least one message is required"));
    }

    [Fact]
    public void ServerToolUseNameRejection_LearnsAllowedServerTools()
    {
        var learner = new FoundryRejectionLearner();
        const string error = "messages.143.content.1.server_tool_use.name: Input should be 'web_search', 'web_fetch', 'code_execution'";

        Assert.True(learner.TryLearn(error));
        Assert.Equal(["code_execution", "web_fetch", "web_search"], learner.AllowedServerTools.Order());
    }
}

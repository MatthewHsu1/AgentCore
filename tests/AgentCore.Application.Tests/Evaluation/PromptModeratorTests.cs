using AgentCore.Application.Evaluation;
using AgentCore.Application.Tests.Evaluation.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Evaluation;

/// <summary>
/// The seam that turns a moderation evaluation into the fact the audit chain records.
/// </summary>
public sealed class PromptModeratorTests
{
    [Fact]
    public async Task AnEndpointThatFlagsNothing_NamesNoCategory()
    {
        PromptModerator moderator = new(ScriptedModerationEvaluator.Clean());

        var categories = await moderator.FlaggedCategoriesAsync("where is my order", TestContext.Current.CancellationToken);

        Assert.Empty(categories);
    }

    [Fact]
    public async Task AnEndpointThatFlags_NamesEveryCategoryInTheOrderItReturnedThem()
    {
        PromptModerator moderator = new(ScriptedModerationEvaluator.Flagging("violence", "harassment"));

        var categories = await moderator.FlaggedCategoriesAsync("...", TestContext.Current.CancellationToken);

        // The order is the endpoint's, and nothing sorts it. AuditPayloadKeys.ModerationCategories
        // promises a reader that order, so a sort here would break the promise at the far end.
        Assert.Equal(["violence", "harassment"], categories);
    }

    [Fact]
    public async Task TheModeratedText_IsWhatTheCallerSaid()
    {
        var endpoint = ScriptedModerationEvaluator.Clean();
        PromptModerator moderator = new(endpoint);

        await moderator.FlaggedCategoriesAsync("how do I reset the console", TestContext.Current.CancellationToken);

        Assert.Equal(["how do I reset the console"], endpoint.Moderated);
    }

    [Fact]
    public async Task AnEndpointThatDidNotAnswer_ReadsAsNothingFlagged()
    {
        // Fail open. A vendor outage must not refuse every caller, so an evaluation carrying no
        // verdict reads the same as a clean one here. CallSession tells the two apart on a metric.
        PromptModerator moderator = new(ScriptedModerationEvaluator.Unanswered());

        var categories = await moderator.FlaggedCategoriesAsync("...", TestContext.Current.CancellationToken);

        Assert.Empty(categories);
    }

    [Fact]
    public async Task AnEndpointThatThrows_Propagates()
    {
        // The moderator does not swallow. CallSession owns the catch, because it owns the log line
        // and the metric that report a turn went unchecked.
        PromptModerator moderator = new(ScriptedModerationEvaluator.Throwing(new InvalidOperationException("boom")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await moderator.FlaggedCategoriesAsync("...", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ARegistryWithNoModerationEvaluator_BuildsNoModerator()
        => Assert.Null(PromptModerator.FromRegistry(new EvaluatorRegistry()));

    [Fact]
    public async Task ARegistryWithAModerationEvaluator_BuildsOneThatCallsIt()
    {
        var endpoint = ScriptedModerationEvaluator.Flagging("hate");
        EvaluatorRegistry registry = new();
        registry.Register(PromptModerator.ModerationEvaluatorName, endpoint);

        var moderator = PromptModerator.FromRegistry(registry);

        Assert.NotNull(moderator);
        Assert.Equal(["hate"], await moderator.FlaggedCategoriesAsync("...", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ARegistryHoldingOnlyAnotherEvaluator_BuildsNoModerator()
    {
        EvaluatorRegistry registry = new();
        registry.Register("fault_code", new FaultCodeEvaluator());

        Assert.Null(PromptModerator.FromRegistry(registry));
    }

    [Fact]
    public void TheModeratorRefusesANullEvaluator()
        => Assert.Throws<ArgumentNullException>(() => new PromptModerator(null!));

    [Fact]
    public void FromRegistryRefusesANullRegistry()
        => Assert.Throws<ArgumentNullException>(() => PromptModerator.FromRegistry(null!));

    [Fact]
    public async Task TheModeratorRefusesNullText()
    {
        PromptModerator moderator = new(ScriptedModerationEvaluator.Clean());

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await moderator.FlaggedCategoriesAsync(null!, TestContext.Current.CancellationToken));
    }
}

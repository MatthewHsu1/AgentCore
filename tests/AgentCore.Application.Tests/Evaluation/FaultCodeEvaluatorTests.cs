using AgentCore.Application.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AgentCore.Application.Tests.Evaluation;

/// <summary>
/// The custom evaluator decision D13 names: fault-code accuracy over access path 1.
/// </summary>
/// <remarks>
/// Decision D10 makes the lookup deterministic and section 9 says the file contents are not. The
/// evaluator therefore measures the reply against what the lookup resolved. It calls no model, so
/// every row here is a set comparison with one answer.
/// </remarks>
public sealed class FaultCodeEvaluatorTests
{
    private static readonly ChatMessage[] Turn =
        [new(ChatRole.User, "My treadmill shows an error. What is wrong?")];

    private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static async Task<BooleanMetric> EvaluateAsync(
        FaultCodeEvaluator evaluator,
        string reply,
        params string[] resolved)
    {
        EvaluationResult result = await evaluator.EvaluateAsync(
            Turn,
            Reply(reply),
            additionalContext: [new FaultCodeContext(resolved)],
            cancellationToken: TestContext.Current.CancellationToken);

        return result.Get<BooleanMetric>(FaultCodeEvaluator.FaultCodeAccuracyMetricName);
    }

    // ---------------------------------------------------------------------------------------------
    // The metric the evaluator reports.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TheEvaluatorNamesOneMetric()
    {
        Assert.Equal([FaultCodeEvaluator.FaultCodeAccuracyMetricName], new FaultCodeEvaluator().EvaluationMetricNames);
    }

    // ---------------------------------------------------------------------------------------------
    // The reply states what the lookup resolved.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AReplyThatStatesTheResolvedCode_Passes()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(),
            "The console shows E7. That means the motor controller lost the speed signal.",
            "E7");

        Assert.True(metric.Value);
        Assert.False(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task AReplyThatStatesEveryResolvedCode_Passes()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(),
            "Two codes apply here. E7 covers the speed signal and E2 covers the incline motor.",
            "E7",
            "E2");

        Assert.True(metric.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // The first failure: the reply drops a code the lookup resolved.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AReplyThatOmitsTheResolvedCode_Fails()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(),
            "That fault means the motor controller lost the speed signal.",
            "E7");

        Assert.False(metric.Value);
        Assert.Contains("E7", metric.Reason, StringComparison.Ordinal);
        Assert.True(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task AReplyThatOmitsOneOfTwoResolvedCodes_Fails()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(),
            "The console shows E7, which covers the speed signal.",
            "E7",
            "E2");

        Assert.False(metric.Value);
        Assert.Contains("E2", metric.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyThatChangesTheCase_Fails()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(),
            "The console shows e7, which covers the speed signal.",
            "E7");

        Assert.False(metric.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // The second failure: the reply states a code the lookup did not resolve.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AReplyThatInventsACode_Fails()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(),
            "The console shows E7. Check E9 as well, because it usually follows.",
            "E7");

        Assert.False(metric.Value);
        Assert.Contains("E9", metric.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyThatStatesACodeWhenTheLookupResolvedNone_Fails()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(),
            "That reading is code LS-12 on this model.");

        Assert.False(metric.Value);
        Assert.Contains("LS-12", metric.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyThatStatesNoCodeWhenTheLookupResolvedNone_Passes()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(),
            "Please read me the message on the console and I will look it up.");

        Assert.True(metric.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // The shape of a code belongs to the knowledge base.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AHostPattern_ReadsTheCodesThatHostWrites()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(@"\bERR[0-9]{3}\b"),
            "The console shows ERR104. It also reports ERR250.",
            "ERR104");

        Assert.False(metric.Value);
        Assert.Contains("ERR250", metric.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostPattern_IgnoresAShapeItDoesNotName()
    {
        BooleanMetric metric = await EvaluateAsync(
            new FaultCodeEvaluator(@"\bERR[0-9]{3}\b"),
            "The console shows ERR104 on the F85 frame.",
            "ERR104");

        Assert.True(metric.Value);
    }

    [Fact]
    public void AnEmptyPattern_FailsAtStartup()
    {
        Assert.Throws<ArgumentException>(() => new FaultCodeEvaluator(string.Empty));
    }

    // ---------------------------------------------------------------------------------------------
    // Without the context the evaluator knows nothing, and says so.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task WithoutTheContext_TheEvaluatorReportsNoValueAndAnError()
    {
        EvaluationResult result = await new FaultCodeEvaluator().EvaluateAsync(
            Turn,
            Reply("The console shows E7."),
            cancellationToken: TestContext.Current.CancellationToken);

        BooleanMetric metric = result.Get<BooleanMetric>(FaultCodeEvaluator.FaultCodeAccuracyMetricName);
        Assert.Null(metric.Value);
        Assert.True(metric.ContainsDiagnostics(diagnostic => diagnostic.Severity == EvaluationDiagnosticSeverity.Error));
        Assert.False(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task TheEvaluatorReadsTheContextOutOfAMixedList()
    {
        EvaluationResult result = await new FaultCodeEvaluator().EvaluateAsync(
            Turn,
            Reply("The console shows E7."),
            additionalContext: [new OtherContext(), new FaultCodeContext(["E7"])],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Get<BooleanMetric>(FaultCodeEvaluator.FaultCodeAccuracyMetricName).Value);
    }

    // ---------------------------------------------------------------------------------------------
    // The context.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TheContextCarriesTheCodesTheLookupResolved()
    {
        FaultCodeContext context = new(["E7", "E2"]);

        Assert.Equal(["E7", "E2"], context.Codes);
        Assert.Equal(FaultCodeContext.FaultCodeContextName, context.Name);
    }

    [Fact]
    public void TheContextReadsALazySequenceOnce()
    {
        int enumerations = 0;
        FaultCodeContext context = new(Codes());

        Assert.Equal(["E7"], context.Codes);
        Assert.Equal(1, enumerations);

        IEnumerable<string> Codes()
        {
            enumerations++;
            yield return "E7";
        }
    }

    /// <summary>A context the evaluator must walk past.</summary>
    private sealed class OtherContext() : EvaluationContext("Other", "text");
}

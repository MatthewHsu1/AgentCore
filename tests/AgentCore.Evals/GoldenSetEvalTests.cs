using AgentCore.Application.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// The offline retrieval gate of D13, over the golden set a deployment owns.
/// </summary>
/// <remarks>
/// <para>
/// This suite measures one deployment's knowledge base and its retrieval settings. Neither lives in
/// this repository, so it runs only where <c>AGENTCORE_EVAL_DATASET</c>, <c>AGENTCORE_EVAL_CONFIG</c>
/// and <c>AGENTCORE_TEST_QDRANT</c> all name something. On <c>main</c> it skips, and
/// <see cref="FixtureEvalTests"/> is the suite that proves the row format still parses.
/// </para>
/// <para>
/// A red result here means the deployment's knowledge base or retrieval settings changed, not that
/// the library broke. The usual causes are a renamed document, a re-chunked knowledge base, a new
/// embedding model, and a lowered score floor.
/// </para>
/// <para>
/// This was two suites until the port was narrowed to one method. The other one ran the same search
/// against the same store and asserted the same set membership with a plainer message; with no id
/// lookup left, it could no longer tell "the document is gone" from "the search did not rank it",
/// which was the only thing it added. Its message survives here, on the failure it was written for.
/// </para>
/// </remarks>
[Collection(EvalStoreCollection.Name)]
public sealed class GoldenSetEvalTests(DatasetFixture fixture) : IClassFixture<DatasetFixture>
{
    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(GoldenSet.Dataset), MemberType = typeof(GoldenSet))]
    public async Task Retrieval_DatasetRow_ReturnsEveryExpectedDocument(GoldenRow row)
    {
        var harness = Require();

        // Arrange, Act
        // Ruling 14: this harness opens no KnowledgeScope. See DatasetHarness for why requireScope is
        // false and no per-row scope is meaningful here.
        var hits = await harness.Search.SearchAsync(row.Query, TestContext.Current.CancellationToken);
        var ids = hits.Select(card => card.CardId).Distinct(StringComparer.Ordinal).ToArray();

        await using ScenarioRun run = await harness.Reporting.CreateScenarioRunAsync(
            row.Id,
            iterationName: "retrieval",
            cancellationToken: TestContext.Current.CancellationToken);

        EvaluationResult result = await run.EvaluateAsync(
            messages: [new ChatMessage(ChatRole.User, row.Query)],
            modelResponse: new ChatResponse(),
            additionalContext: [new RetrievedDocumentsContext(row.ExpectedDocumentIds, ids)],
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var recall = result.Get<NumericMetric>(DocumentRecallEvaluator.DocumentRecallMetricName);
        var missing = row.ExpectedDocumentIds
            .Where(id => !ids.Contains(id, StringComparer.Ordinal))
            .ToArray();

        // The evaluator's own Reason reads as a retrieval failure, and the usual cause is not one: a
        // row goes stale when its document is renamed or deleted. Naming the ids that did not come
        // back is what separates the two without a second run.
        Assert.False(
            recall.Interpretation!.Failed,
            missing.Length == 0
                ? $"row {row.Id}: {recall.Reason}"
                : $"row {row.Id} names {string.Join(", ", missing)}, and the search did not return it. "
                    + $"Either the document moved or was renamed, or the row is stale. {recall.Reason}");
    }

    private DatasetHarness Require()
    {
        Assert.SkipUnless(fixture.Harness is not null, EvalHarness.DatasetSkipReason ?? string.Empty);

        return fixture.Harness!;
    }
}

using System.Text;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// The offline gate of D13, over the golden set a deployment owns.
/// </summary>
/// <remarks>
/// <para>
/// This suite measures the knowledge base, the retrieval settings, and the reply. None of those three
/// lives in this repository, so it runs only where <c>AGENTCORE_EVAL_DATASET</c> and
/// <c>AGENTCORE_EVAL_CONFIG</c> name them. On <c>main</c> it skips, and <see cref="FixtureEvalTests"/>
/// is the suite that proves the harness still works.
/// </para>
/// <para>
/// A red result here means the deployment changed, not that the library broke. The usual causes are a
/// renamed document, a re-chunked knowledge base, a new embedding model, and a lower search limit.
/// </para>
/// </remarks>
[Collection(EvalStoreCollection.Name)]
public sealed class GoldenSetEvalTests(DatasetFixture fixture) : IClassFixture<DatasetFixture>
{
    private const string GroundedInstruction =
        "Answer the question using only the passages below. State nothing the passages do not state.";

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(GoldenSet.Dataset), MemberType = typeof(GoldenSet))]
    public async Task Retrieval_DatasetRow_ReturnsEveryExpectedDocument(GoldenRow row)
    {
        var harness = Require();

        // Arrange
        var (ids, _) = await RetrieveAsync(harness.Search, row.Query);

        // Act
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
        Assert.False(recall.Interpretation!.Failed, $"row {row.Id}: {recall.Reason}");
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(GoldenSet.Dataset), MemberType = typeof(GoldenSet))]
    public async Task Generation_DatasetRow_StaysGroundedInTheRetrievedPassages(GoldenRow row)
    {
        var harness = Require();

        Assert.SkipUnless(
            harness.HasJudge,
            "the document names no evaluation.judge, so no judged metric can run.");

        // Arrange
        var (_, passages) = await RetrieveAsync(harness.Search, row.Query);

        // Act
        // The reply model writes the answer over the passages the search returned. The eval response
        // cache does not cover this call, so a rerun pays for one generation for each row.
        ChatResponse reply = await harness.Reply!.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, $"{GroundedInstruction}\n\n{passages}"),
                new ChatMessage(ChatRole.User, row.Query),
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        await using ScenarioRun run = await harness.Reporting.CreateScenarioRunAsync(
            row.Id,
            iterationName: "generation",
            cancellationToken: TestContext.Current.CancellationToken);

        EvaluationResult result = await run.EvaluateAsync(
            messages: [new ChatMessage(ChatRole.User, row.Query)],
            modelResponse: reply,
            additionalContext:
            [
                new GroundednessEvaluatorContext(passages),
                new FaultCodeContext(row.ExpectedFaultCodes),
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var groundedness = result.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName);
        Assert.False(groundedness.Interpretation!.Failed, $"row {row.Id}: {groundedness.Reason}");
    }

    private DatasetHarness Require()
    {
        Assert.SkipUnless(
            fixture.Harness is not null,
            $"{GoldenSet.DatasetVariable} and {EvalHarness.ConfigurationVariable} name no golden set.");

        return fixture.Harness!;
    }

    /// <summary>Runs one search, and returns what came back and the text of it.</summary>
    private static async Task<(string[] Ids, string Passages)> RetrieveAsync(
        IKnowledgeRetrievalPort search,
        string query)
    {
        IReadOnlyList<KnowledgeChunk> hits = await search.SearchAsync(
            query,
            EvalHarness.SearchLimit,
            TestContext.Current.CancellationToken);

        string[] ids = [.. hits.Select(chunk => chunk.DocumentId).Distinct(StringComparer.Ordinal)];

        StringBuilder text = new();
        foreach (var chunk in hits)
        {
            text.AppendLine(chunk.Text);
        }

        return (ids, text.ToString());
    }
}

using AgentCore.Application.Evaluation;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// The harness self-test. It runs everywhere, and it reads no key.
/// </summary>
/// <remarks>
/// <para>
/// This suite proves that the loader parses a row, the store ranks, the evaluator scores, and the
/// disk store writes a result <c>dotnet aieval report</c> can render. The knowledge base is five
/// invented files, so the score is not a quality number and it must not be read as one.
/// </para>
/// <para>
/// The gate is exact. Five short files with no ambiguity always return the answer, so a recall below
/// 1 means the harness broke and never that retrieval is weak. That is the whole value of this suite:
/// when the golden-set gate on a deployment branch goes red, this result says which of the two broke.
/// </para>
/// </remarks>
public sealed class FixtureEvalTests
{
    [Theory]
    [MemberData(nameof(GoldenSet.Fixture), MemberType = typeof(GoldenSet))]
    public async Task Search_FixtureRow_ReturnsEveryExpectedDocument(GoldenRow row)
    {
        // Arrange
        var (search, _) = EvalHarness.OpenFixtureKnowledge();
        var reporting = EvalHarness.ForFixture();

        // Act
        IReadOnlyList<KnowledgeChunk> hits = await search.SearchAsync(
            row.Query,
            EvalHarness.SearchLimit,
            TestContext.Current.CancellationToken);

        string[] documents = [.. hits.Select(chunk => chunk.DocumentId).Distinct(StringComparer.Ordinal)];

        await using ScenarioRun run = await reporting.CreateScenarioRunAsync(
            row.Id,
            cancellationToken: TestContext.Current.CancellationToken);

        EvaluationResult result = await run.EvaluateAsync(
            messages: [new ChatMessage(ChatRole.User, row.Query)],
            modelResponse: new ChatResponse(),
            additionalContext: [new RetrievedDocumentsContext(row.ExpectedDocumentIds, documents)],
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var recall = result.Get<NumericMetric>(DocumentRecallEvaluator.DocumentRecallMetricName);
        Assert.Equal(1.0, recall.Value);
    }

    [Fact]
    public void Fixture_HoldsEnoughRowsToExerciseTheFormat()
    {
        // A row with one expected document and a row with two exercise both branches of the metric.

        // Arrange, Act
        var rows = GoldenSet.Load(GoldenSet.FixturePath);

        // Assert
        Assert.Contains(rows, row => row.ExpectedDocumentIds.Count == 1);
        Assert.Contains(rows, row => row.ExpectedDocumentIds.Count > 1);
        Assert.Contains(rows, row => row.ExpectedFaultCodes.Count > 0);
    }
}

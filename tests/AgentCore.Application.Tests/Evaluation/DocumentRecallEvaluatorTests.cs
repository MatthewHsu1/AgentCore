using AgentCore.Application.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AgentCore.Application.Tests.Evaluation;

/// <summary>
/// The retrieval half of the offline set: did the search return the file that holds the answer.
/// </summary>
/// <remarks>
/// The design measures the file and not the passage, so every row here compares document ids. The
/// evaluator calls no model, so each row has one answer and the suite runs where no key is set.
/// </remarks>
public sealed class DocumentRecallEvaluatorTests
{
    private const string AnswerDocument = "faults/e7.md";
    private const string OtherDocument = "manual/folding.md";

    private static readonly ChatMessage[] Turn =
        [new(ChatRole.User, "My treadmill shows E7 and stops.")];

    private static async Task<NumericMetric> EvaluateAsync(string[] expected, string[] retrieved)
    {
        EvaluationResult result = await new DocumentRecallEvaluator().EvaluateAsync(
            Turn,
            new ChatResponse(),
            additionalContext: [new RetrievedDocumentsContext(expected, retrieved)],
            cancellationToken: TestContext.Current.CancellationToken);

        return result.Get<NumericMetric>(DocumentRecallEvaluator.DocumentRecallMetricName);
    }

    [Fact]
    public void TheEvaluatorNamesOneMetric()
    {
        // Arrange
        var evaluator = new DocumentRecallEvaluator();

        // Act
        var names = evaluator.EvaluationMetricNames;

        // Assert
        Assert.Equal([DocumentRecallEvaluator.DocumentRecallMetricName], names);
    }

    [Fact]
    public async Task EvaluateAsync_SearchReturnsTheExpectedDocument_ScoresOneAndPasses()
    {
        // Arrange, Act
        var metric = await EvaluateAsync([AnswerDocument], [AnswerDocument]);

        // Assert
        Assert.Equal(1.0, metric.Value);
        Assert.False(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task EvaluateAsync_SearchReturnsTheExpectedDocumentAmongOthers_ScoresOne()
    {
        // The metric is recall and never precision. A store that returns the answer plus other files
        // still answered the question, because the model reads what comes back.

        // Arrange, Act
        var metric = await EvaluateAsync([AnswerDocument], [OtherDocument, AnswerDocument]);

        // Assert
        Assert.Equal(1.0, metric.Value);
        Assert.False(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task EvaluateAsync_SearchMissesTheExpectedDocument_ScoresZeroAndFails()
    {
        // Arrange, Act
        var metric = await EvaluateAsync([AnswerDocument], [OtherDocument]);

        // Assert
        Assert.Equal(0.0, metric.Value);
        Assert.True(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task EvaluateAsync_SearchReturnsOneOfTwoExpectedDocuments_ScoresAHalfAndFails()
    {
        // Arrange, Act
        var metric = await EvaluateAsync([AnswerDocument, OtherDocument], [AnswerDocument]);

        // Assert
        Assert.Equal(0.5, metric.Value);
        Assert.True(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task EvaluateAsync_SearchMissesADocument_NamesThatDocumentInTheReason()
    {
        // Arrange, Act
        var metric = await EvaluateAsync([AnswerDocument], [OtherDocument]);

        // Assert
        Assert.Contains(AnswerDocument, metric.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_DocumentIdsDifferInCase_CountsAsAMiss()
    {
        // A document id is compared with Ordinal, the same as every other name in the library.

        // Arrange, Act
        var metric = await EvaluateAsync([AnswerDocument], ["FAULTS/E7.MD"]);

        // Assert
        Assert.Equal(0.0, metric.Value);
    }

    [Fact]
    public async Task EvaluateAsync_NoContext_ReportsNoValueAndDoesNotFail()
    {
        // A row that names nothing measures nothing. It must not read as a pass, and it must not read
        // as a retrieval failure either.

        // Arrange
        var evaluator = new DocumentRecallEvaluator();

        // Act
        EvaluationResult result = await evaluator.EvaluateAsync(
            Turn,
            new ChatResponse(),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var metric = result.Get<NumericMetric>(DocumentRecallEvaluator.DocumentRecallMetricName);
        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.False(metric.Interpretation.Failed);
    }

    [Fact]
    public async Task EvaluateAsync_ContextExpectsNoDocument_ReportsNoValue()
    {
        // Arrange, Act
        var metric = await EvaluateAsync([], [AnswerDocument]);

        // Assert
        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
    }
}

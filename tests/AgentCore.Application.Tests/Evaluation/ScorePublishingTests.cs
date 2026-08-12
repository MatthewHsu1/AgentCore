using AgentCore.Application.Evaluation;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AgentCore.Application.Tests.Evaluation;

/// <summary>
/// The seam that carries a finished evaluation out of the library.
/// </summary>
/// <remarks>
/// The in-memory publisher is the default the seam ships with. It keeps the order scores arrive in,
/// it hands out a copy, and it takes scores from several turns at once.
/// </remarks>
public sealed class ScorePublishingTests
{
    private static EvaluationScore Score(string evaluator, double value) =>
        new(evaluator, new EvaluationResult(new NumericMetric("Recorded", value)));

    [Fact]
    public async Task ThePublisherKeepsTheOrderScoresArriveIn()
    {
        InMemoryEvaluationScorePublisher publisher = new();

        await publisher.PublishAsync(Score("fault_code", 1), TestContext.Current.CancellationToken);
        await publisher.PublishAsync(Score("moderation", 2), TestContext.Current.CancellationToken);

        Assert.Equal(["fault_code", "moderation"], publisher.Scores.Select(score => score.Evaluator));
    }

    [Fact]
    public async Task ThePublisherCarriesTheMetricsUnchanged()
    {
        InMemoryEvaluationScorePublisher publisher = new();
        EvaluationScore score = Score("fault_code", 0.75);

        await publisher.PublishAsync(score, TestContext.Current.CancellationToken);

        EvaluationScore published = Assert.Single(publisher.Scores);
        Assert.Same(score, published);
        Assert.Equal(0.75, published.Result.Get<NumericMetric>("Recorded").Value);
    }

    [Fact]
    public void ANewPublisherHoldsNothing()
    {
        Assert.Empty(new InMemoryEvaluationScorePublisher().Scores);
    }

    [Fact]
    public async Task TheScoresPropertyHandsOutACopy()
    {
        InMemoryEvaluationScorePublisher publisher = new();
        await publisher.PublishAsync(Score("fault_code", 1), TestContext.Current.CancellationToken);

        IReadOnlyList<EvaluationScore> read = publisher.Scores;
        await publisher.PublishAsync(Score("moderation", 2), TestContext.Current.CancellationToken);

        Assert.Single(read);
        Assert.Equal(2, publisher.Scores.Count);
    }

    [Fact]
    public async Task ThePublisherTakesScoresFromManyTurnsAtOnce()
    {
        InMemoryEvaluationScorePublisher publisher = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(index =>
            Task.Run(
                async () => await publisher.PublishAsync(Score("fault_code", index), cancellationToken),
                cancellationToken)));

        Assert.Equal(64, publisher.Scores.Count);
    }

    [Fact]
    public async Task ANullScore_Fails()
    {
        InMemoryEvaluationScorePublisher publisher = new();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await publisher.PublishAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACancelledPublish_Fails()
    {
        InMemoryEvaluationScorePublisher publisher = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await publisher.PublishAsync(Score("fault_code", 1), cancellation.Token));
        Assert.Empty(publisher.Scores);
    }
}

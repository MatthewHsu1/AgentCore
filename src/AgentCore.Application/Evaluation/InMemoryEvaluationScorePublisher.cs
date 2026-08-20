namespace AgentCore.Application.Evaluation;

/// <summary>
/// The publisher that keeps every score in a list.
/// </summary>
/// <remarks>
/// <para>
/// This is the default the seam ships with. It writes nowhere, so a host that registers nothing else
/// still runs, and the offline golden set in <c>tests/AgentCore.Evals</c> reads its scores back
/// without a sink.
/// </para>
/// <para>
/// The list grows without a bound, so a long-running host replaces this publisher rather than
/// keeping it. Scores may arrive from several turns at once, so the append takes a lock.
/// </para>
/// </remarks>
public sealed class InMemoryEvaluationScorePublisher : IEvaluationScorePublisher
{
    private readonly Lock _gate = new();
    private readonly List<EvaluationScore> _scores = [];

    /// <summary>Gets the scores this publisher holds, in the order they arrived.</summary>
    public IReadOnlyList<EvaluationScore> Scores
    {
        get
        {
            lock (_gate)
            {
                return [.. _scores];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(EvaluationScore score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _scores.Add(score);
        }

        return ValueTask.CompletedTask;
    }
}

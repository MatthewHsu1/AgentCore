namespace AgentCore.Application.Evaluation;

/// <summary>
/// The seam that carries a finished evaluation out of the library.
/// </summary>
/// <remarks>
/// <para>
/// An evaluation ends in a score, and the score has to reach something the host owns: a log, a
/// meter, or the audit chain. This is that one seam, so the library never learns which of those it
/// is.
/// </para>
/// <para>
/// A publisher runs after a turn, never inside one. Decision D9 says a judge must never block a
/// turn, and triage row T18 defers the online path, so an implementation that waits is still correct
/// here and would not be correct on the turn loop.
/// </para>
/// </remarks>
public interface IEvaluationScorePublisher
{
    /// <summary>Publishes one finished evaluation.</summary>
    /// <param name="score">The evaluator name and the metrics it produced.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes when the score leaves the library.</returns>
    ValueTask PublishAsync(EvaluationScore score, CancellationToken cancellationToken = default);
}

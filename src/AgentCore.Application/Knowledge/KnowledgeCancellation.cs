namespace AgentCore.Application.Knowledge;

/// <summary>
/// Tells a caller who hung up apart from a retrieval that failed.
/// </summary>
/// <remarks>
/// The two are answered differently everywhere in this folder: a hang-up is rethrown and charges the
/// call nothing, while a failure is reported to the model and logged.
/// </remarks>
internal static class KnowledgeCancellation
{
    /// <summary>Whether a failure is the caller ending the turn.</summary>
    /// <param name="failure">What the search threw.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns><see langword="true"/> when the caller hung up.</returns>
    internal static bool ByCaller(Exception failure, CancellationToken cancellationToken)
        => failure is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>Whether a search under a deadline was ended by the caller rather than by that deadline.</summary>
    /// <param name="failure">What the search threw.</param>
    /// <param name="timeout">The deadline's own source, cancelled by its timer and by nothing else.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns><see langword="true"/> when the caller hung up.</returns>
    /// <remarks>
    /// The caller's token standing cancelled is not enough on its own. A caller that hangs up in the
    /// moment after the deadline fired would make a genuine timeout read as a hang-up, and the refund
    /// that follows would hand the facet back an ask it had already spent — letting a probe that
    /// always times out offer the same facet forever, which is what K22's cap exists to stop. Only the
    /// timer cancels <paramref name="timeout"/>, so a tie is settled in favour of the timeout.
    /// </remarks>
    internal static bool ByCaller(
        Exception failure, CancellationTokenSource timeout, CancellationToken cancellationToken)
        => !timeout.IsCancellationRequested && ByCaller(failure, cancellationToken);
}

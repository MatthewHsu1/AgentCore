using AgentCore.Application.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// Releases the harness background providers' sessions when a call ends, so children still
/// running cannot outlive it. A task that spans turns is untouched until the end.
/// </summary>
internal static class BackgroundSessionRelease
{
    /// <summary>
    /// How long the release waits for running children to acknowledge the cancel before the
    /// call's own teardown continues. Bounded like the extractor deadline: teardown never hangs
    /// on a child.
    /// </summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cancels each provider's running tasks on <paramref name="session"/> and releases the
    /// session. Best effort: one provider's fault is logged and the rest still release.
    /// </summary>
    /// <param name="providers">Every background provider the document compiled.</param>
    /// <param name="session">The session of the call that ended.</param>
    /// <param name="callId">The id of the call, for the log.</param>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="cancellationToken">Cancels the release.</param>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public static async ValueTask ReleaseAsync(
        IReadOnlyList<BackgroundAgentsProvider> providers,
        AgentSession session,
        string callId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var provider in providers)
        {
            try
            {
                await provider.ReleaseSessionAsync(session, cancelRunning: true, ReleaseTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.BackgroundReleaseFailed(logger, callId, exception);
            }
        }
    }
#pragma warning restore MAAI001
}

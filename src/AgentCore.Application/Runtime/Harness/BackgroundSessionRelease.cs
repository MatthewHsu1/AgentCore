using System.Diagnostics;
using AgentCore.Application.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// Releases the harness background providers' sessions when a conversation ends, so children still
    /// running cannot outlive it. A task that spans turns is untouched until the end, unless another session's
    /// turn makes this one rebuild its session from the store: the old session is released then, without the turn
    /// waiting for it.
    /// </summary>
    internal static class BackgroundSessionRelease
    {
        /// <summary>
        /// How long the release waits for running children to acknowledge the cancel before the
        /// conversation's own teardown continues. Bounded like the extractor deadline: teardown never hangs
        /// on a child.
        /// </summary>
        private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How early MAF's timeout can end by <see cref="Stopwatch"/>'s clock: timers run on a coarse tick of a few
        /// milliseconds.
        /// </summary>
        private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Cancels each provider's running tasks on <paramref name="session"/> and releases the
        /// session. Best effort: one provider's fault is logged and the rest still release.
        /// </summary>
        /// <param name="providers">Every background provider the document compiled.</param>
        /// <param name="session">The session of the conversation that ended, or the one a catch-up replaced.</param>
        /// <param name="conversationId">The id of the conversation, for the log.</param>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="cancellationToken">Cancels the release.</param>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
        public static async ValueTask ReleaseAsync(
            IReadOnlyList<BackgroundAgentsProvider> providers,
            AgentSession session,
            string conversationId,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(providers);
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(conversationId);
            ArgumentNullException.ThrowIfNull(logger);

            foreach (BackgroundAgentsProvider provider in providers)
            {
                try
                {
                    long started = Stopwatch.GetTimestamp();
                    
                    await provider.ReleaseSessionAsync(session, cancelRunning: true, ReleaseTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    // MAF gives up on a child that ignores its cancel without saying so; the full wait is the only sign.
                    if (Stopwatch.GetElapsedTime(started) >= ReleaseTimeout - TimerSlack)
                    {
                        Log.BackgroundReleaseTimedOut(logger, conversationId, ReleaseTimeout.TotalSeconds);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Log.BackgroundReleaseFailed(logger, conversationId, exception);
                }
            }
        }
#pragma warning restore MAAI001
    }
}

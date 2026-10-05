// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/utils/aio/utils.py
// (cancel_and_wait) and livekit-agents/livekit/agents/utils/log.py (log_exceptions),
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

namespace AgentCore.AspNetCore.Voice.Threading
{
    /// <summary>Cancellation and fault-logging helpers a speech task's owner tears it down with.</summary>
    internal static class TaskTeardown
    {
        /// <summary>Cancels <paramref name="cancellation"/>, then waits for every task to finish.</summary>
        /// <param name="cancellation">Cancelled once, before any task is awaited.</param>
        /// <param name="tasks">The tasks racing that cancellation.</param>
        /// <returns>
        /// A task that completes once every task in <paramref name="tasks"/> has finished, however it
        /// finished. A task's own fault is not raised here: its <see cref="LogExceptions"/> wrapper
        /// already reported it.
        /// </returns>
        public static async Task CancelAndWaitAsync(CancellationTokenSource cancellation, IEnumerable<Task> tasks)
        {
            cancellation.Cancel();

            foreach (Task task in tasks)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Waited on, never raised: LiveKit's cancel_and_wait awaits a done-callback, not the task.
                }
            }
        }

        /// <summary>Wraps a speech task body so a fault is logged once, then rethrown to its owner.</summary>
        /// <param name="action">The task body to run.</param>
        /// <param name="logFault">Logs a fault <paramref name="action"/> raised. Must not throw.</param>
        public static Func<Task> LogExceptions(Func<Task> action, Action<Exception> logFault)
        {
            return async () =>
            {
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logFault(ex);
                    throw;
                }
            };
        }
    }
}

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>Which task an observer is watching, so it logs the right line.</summary>
    internal enum ConnectionTaskKind
    {
        /// <summary>The loop that reads the transport.</summary>
        ReadLoop,

        /// <summary>The loop that writes the transport.</summary>
        WriteLoop,

        /// <summary>The wait for the conversation to end: its words reach store 1, then its session goes.</summary>
        SessionClose,
    }

    /// <summary>
    /// Awaits the loops and the session close of one connection, and never lets a fault go unobserved.
    /// </summary>
    /// <param name="conversationId">Names the conversation in a log line. A function, because the id arrives mid-conversation.</param>
    /// <param name="logTimeout">Logs that a task passed its teardown deadline.</param>
    /// <param name="logFault">Logs a fault this observer could not classify.</param>
    /// <param name="classify">
    /// Gives an adapter first refusal on an exception, so a vendor-specific cause can be logged in its
    /// own words. It returns <see langword="true"/> when it handled the exception, and
    /// <see langword="false"/> to fall through to <paramref name="logFault"/>. It must not throw.
    /// </param>
    internal sealed class ConnectionTaskObserver(
        Func<string> conversationId,
        Action<string, string> logTimeout,
        Action<ConnectionTaskKind, string, Exception> logFault,
        Func<Exception, ConnectionTaskKind, string, bool> classify)
    {
        /// <summary>Awaits a loop or the session close, and never lets its fault go unobserved.</summary>
        /// <param name="task">The read loop's task, the write loop's task, or the session close.</param>
        /// <param name="kind">Which log line names the fault, if there is one.</param>
        /// <param name="timeout">
        /// How long to wait before giving up on <paramref name="task"/>, or <see langword="null"/> to
        /// wait for it unconditionally.
        /// </param>
        /// <returns>A task that completes once <paramref name="task"/> has been observed.</returns>
        public async Task ObserveAsync(Task task, ConnectionTaskKind kind, TimeSpan? timeout = null)
        {
            try
            {
                if (timeout is { } bound)
                {
                    await task.WaitAsync(bound).ConfigureAwait(false);
                }
                else
                {
                    await task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // This connection's own token just cancelled it. That is teardown, not a fault.
            }
            catch (TimeoutException) when (timeout is not null)
            {
                SafeLog(() => logTimeout(conversationId(), DisplayName(kind)));

                // Task.WaitAsync removes its own continuation from task once the timeout fires, rather
                // than leaving one behind to observe a later fault: a probe confirmed this directly. So
                // if task faults afterward with nobody else watching, that fault surfaces only as an
                // UnobservedTaskException raised during a GC — which produces no log line, and, under
                // the default ThrowUnobservedTaskExceptions setting, does not crash the process either.
                // Without a continuation of our own here, the real fault behind this timeout — a model
                // fault, a tool fault, or the ObjectDisposedException from the token source teardown
                // is about to dispose — would stay exactly that unseen. This continuation is what makes
                // it visible instead. It does not await task, so it does not block; it can run inline on
                // this thread if task happens to fault in the narrow window between the timeout firing
                // and this line attaching it, which ExecuteSynchronously allows and which is harmless
                // either way; and it must not throw, which LogFault's own SafeLog guard below
                // guarantees.
                _ = task.ContinueWith(
                    completed => LogFault(kind, completed.Exception?.GetBaseException()),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception fault) when (SafeClassify(fault, kind, conversationId()))
            {
                // The adapter recognised this one and logged it in its own words. It sits here, after
                // the two clauses above and before the general one below, because a cause a vendor can
                // name is more specific than this connection's own teardown but less specific than a
                // cancellation it asked for itself.
            }
            catch (Exception fault)
            {
                LogFault(kind, fault);
            }
        }

        /// <summary>Runs one log conversation, and never lets it break the teardown sequence around it.</summary>
        /// <param name="log">The conversation to make, already bound to its logger and its arguments.</param>
        public static void SafeLog(Action log)
        {
            try
            {
                log();
            }
            catch (Exception)
            {
                // Nothing above this point can safely observe a logging fault either, so it stops here.
            }
        }

        /// <summary>Names one <see cref="ConnectionTaskKind"/> for a log line, in the sentence it reads in.</summary>
        /// <param name="kind">Which task the sentence is about.</param>
        /// <returns>The name to read in that sentence.</returns>
        private static string DisplayName(ConnectionTaskKind kind)
        {
            return kind switch
            {
                ConnectionTaskKind.ReadLoop => "the read loop",
                ConnectionTaskKind.WriteLoop => "the write loop",
                ConnectionTaskKind.SessionClose => "the session close",
                _ => "a task",
            };
        }

        /// <summary>Hands a fault to <c>logFault</c>, whichever <paramref name="kind"/> names.</summary>
        /// <param name="kind">Which task faulted.</param>
        /// <param name="fault">The cause, or <see langword="null"/> when none was available.</param>
        private void LogFault(ConnectionTaskKind kind, Exception? fault)
        {
            if (fault is null)
            {
                return;
            }

            SafeLog(() => logFault(kind, conversationId(), fault));
        }

        /// <summary>Runs <c>classify</c> behind the same guard <see cref="SafeLog"/> uses.</summary>
        /// <param name="fault">The exception the adapter gets first refusal on.</param>
        /// <param name="kind">Which task faulted.</param>
        /// <param name="id">The conversation the fault belongs to.</param>
        /// <returns>
        /// <see langword="true"/> when the adapter handled <paramref name="fault"/> itself, and
        /// <see langword="false"/> when it did not — including when it threw.
        /// </returns>
        private bool SafeClassify(Exception fault, ConnectionTaskKind kind, string id)
        {
            try
            {
                return classify(fault, kind, id);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

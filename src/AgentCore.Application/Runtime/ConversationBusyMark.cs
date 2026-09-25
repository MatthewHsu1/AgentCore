using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The conversation's busy mark in the store.
    /// </summary>
    internal sealed class ConversationBusyMark
    {
        /// <summary>
        /// How long a mark lives after it was last put. A host that crashed frees the conversation this long after
        /// its last renewal.
        /// </summary>
        internal static readonly TimeSpan Lease = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long a turn waits for another session's mark before it is refused. It covers a stopped turn's work after
        /// the reply and the filing after the abort, each bounded by <see cref="ConversationSession.TurnCompletionTimeout"/>,
        /// and the <see cref="Lease"/> of a holder that crashed.
        /// </summary>
        internal static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        private static readonly TimeSpan RenewEvery = Lease / 3;

        /// <summary>How long a waiting turn first waits before it asks for the mark again. Each wait doubles, up to 250 ms.</summary>
        internal static readonly TimeSpan FirstPoll = TimeSpan.FromMilliseconds(50);

        private static readonly TimeSpan LastPoll = TimeSpan.FromMilliseconds(250);

        private readonly ConversationSession _session;

        private readonly string _holder = Guid.NewGuid().ToString("N");

        private readonly Lock _gate = new();

        // Guarded by _gate. _holds counts the holders that entered and have not left, waiting ones included.
        private int _holds;

        private bool _held;

        private ITimer? _renewal;

        private Task _writes = Task.CompletedTask;

        internal ConversationBusyMark(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <summary>
        /// Holds the mark: takes it from the store unless this session already holds it, waiting up to
        /// <see cref="WaitLimit"/> while another session's mark is live. Each call is paired with one <see cref="Exit"/>,
        /// made only once this returned.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <exception cref="ConversationTurnConflictException">Another session held the mark past the limit.</exception>
        internal async ValueTask EnterAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _holds++;
                if (_held)
                {
                    return;
                }
            }

            // Two turns of one session that take at once both succeed: they mark under the same holder.
            try
            {
                await TakeAsync(cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    _held = true;
                    _renewal ??= _session.Time.CreateTimer(_ => Renew(), state: null, RenewEvery, RenewEvery);
                }
            }
            catch
            {
                _ = Exit();
                throw;
            }
        }

        /// <summary>Releases one hold. The last one stops the renewal and clears the mark in the store.</summary>
        /// <returns>The clear, for a host that answers only once the conversation is free. It never faults.</returns>
        internal Task Exit()
        {
            lock (_gate)
            {
                if (--_holds > 0)
                {
                    return Task.CompletedTask;
                }

                _held = false;
                _renewal?.Dispose();
                _renewal = null;
            }

            return Enqueue(ClearAsync);
        }

        private async Task TakeAsync(CancellationToken cancellationToken)
        {
            long started = _session.Time.GetTimestamp();
            TimeSpan poll = FirstPoll;

            try
            {
                while (!await Enqueue(() => MarkAsync(cancellationToken)).ConfigureAwait(false))
                {
                    TimeSpan left = WaitLimit - _session.Time.GetElapsedTime(started);
                    if (left <= TimeSpan.Zero)
                    {
                        TurnRefusals.Raise(_session, turnIndex: null, TurnRefusals.Busy);
                        throw new ConversationTurnConflictException(
                            $"Another request still held conversation '{_session.ConversationId}' after {WaitLimit.TotalSeconds:0} s, "
                            + "so this turn was refused before it ran.");
                    }

                    await Task.Delay(poll < left ? poll : left, _session.Time, cancellationToken).ConfigureAwait(false);
                    poll = poll * 2 < LastPoll ? poll * 2 : LastPoll;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TurnRefusals.Raise(_session, turnIndex: null, TurnRefusals.Gone);
                throw;
            }
        }

        private async Task<bool> MarkAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _session.Compiled.ConversationStore
                    .TryMarkBusyAsync(_session.ConversationId, _holder, Lease, cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A store that cannot be marked never stops a turn: the store's turn check stays the backstop.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                Log.BusyMarkFailed(_session.Logger, _session.ConversationId, exception);
                return true;
            }
        }

        private void Renew()
        {
            _ = Enqueue(async () =>
            {
                lock (_gate)
                {
                    if (!_held)
                    {
                        return false;
                    }
                }

                try
                {
                    if (!await _session.Compiled.ConversationStore
                        .TryMarkBusyAsync(_session.ConversationId, _holder, Lease, CancellationToken.None)
                        .ConfigureAwait(false))
                    {
                        Log.BusyMarkLost(_session.Logger, _session.ConversationId);
                    }
                }
#pragma warning disable CA1031 // A renewal that fails is retried by the next tick, and the mark lapses at worst.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    Log.BusyMarkFailed(_session.Logger, _session.ConversationId, exception);
                }

                return true;
            });
        }

        private async Task<bool> ClearAsync()
        {
            lock (_gate)
            {
                // A turn entered since the release: its own mark takes this one over, so clearing would free a busy conversation.
                if (_holds > 0)
                {
                    return false;
                }
            }

            try
            {
                await _session.Compiled.ConversationStore
                    .ClearBusyAsync(_session.ConversationId, _holder, CancellationToken.None)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A mark that cannot be cleared lapses after its lease.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                Log.BusyMarkFailed(_session.Logger, _session.ConversationId, exception);
            }

            return true;
        }

        /// <summary>
        /// Runs one store write of the mark after every write asked for before it. With none pending it runs at once,
        /// on the caller's thread, so a store that answers synchronously starts a turn synchronously too.
        /// </summary>
        private Task<bool> Enqueue(Func<Task<bool>> write)
        {
            TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task before;

            lock (_gate)
            {
                before = _writes;
                _writes = written.Task;
            }

            return AfterAsync(before, write, written);
        }

        private static async Task<bool> AfterAsync(Task before, Func<Task<bool>> write, TaskCompletionSource written)
        {
            try
            {
                await before.ConfigureAwait(false);
                return await write().ConfigureAwait(false);
            }
            finally
            {
                written.SetResult();
            }
        }
    }
}

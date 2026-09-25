namespace AgentCore.Application.Runtime
{
    internal sealed class ConversationCutTracker
    {
        private readonly ConversationSession _session;

        private bool _running;

        private TaskCompletionSource? _freed;

        private bool _closed;

        private TurnCutSlot? _slot;

        private int _slotTurnIndex;

        private int _newestStarted = -1;

        internal ConversationCutTracker(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <summary>Cuts one turn's reply by the Cut rule (design section 3).</summary>
        /// <param name="turnIndex">The turn the user was seeing or hearing.</param>
        /// <param name="cut">What reached the user.</param>
        /// <returns>
        /// <see langword="true"/> when the running turn took the cut, or the committed turn was rewritten to it;
        /// <see langword="false"/> for a turn older than the newest turn started, which is left as it is.
        /// </returns>
        internal bool Cut(int turnIndex, TurnCut cut)
        {
            ArgumentNullException.ThrowIfNull(cut);
            if (cut.Played is { } played)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(played, TimeSpan.Zero, nameof(cut));
            }

            lock (_session.TurnLock)
            {
                if (turnIndex < _newestStarted)
                {
                    return false;
                }

                // A slot that refuses the cut has committed its turn, which is then the last turn.
                if (_slot is { } slot && _slotTurnIndex == turnIndex && slot.TryCut(cut))
                {
                    return true;
                }

                return AmendTurn(turnIndex, cut, recut: false);
            }
        }

        /// <summary>Replaces the cut one turn already took, while it is the newest turn started.</summary>
        /// <param name="turnIndex">The turn the user was seeing or hearing.</param>
        /// <param name="cut">A later account of what reached the user.</param>
        /// <returns>
        /// <see langword="true"/> when the running turn took the recut, or the committed turn was rewritten to it;
        /// <see langword="false"/> for a turn that took no cut, or one older than the newest turn started.
        /// </returns>
        internal bool Recut(int turnIndex, TurnCut cut)
        {
            ArgumentNullException.ThrowIfNull(cut);
            if (cut.Played is { } played)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(played, TimeSpan.Zero, nameof(cut));
            }

            lock (_session.TurnLock)
            {
                if (turnIndex < _newestStarted)
                {
                    return false;
                }

                if (_slot is { } slot && _slotTurnIndex == turnIndex && slot.TryRecut(cut))
                {
                    return true;
                }

                return AmendTurn(turnIndex, cut, recut: true);
            }
        }

        /// <summary>
        /// Opens the window in which <see cref="Cut"/> reaches this turn.
        /// </summary>
        /// <param name="turnIndex">The index of the turn about to run.</param>
        /// <param name="cancellationToken">The token of the host.</param>
        /// <returns>The slot whose token the run reads. The caller ends it with <see cref="EndRun"/>.</returns>
        internal TurnCutSlot StartRun(int turnIndex, CancellationToken cancellationToken)
        {
            TurnCutSlot slot = new(_session.TurnLock, cancellationToken);

            lock (_session.TurnLock)
            {
                _slot = slot;
                _slotTurnIndex = turnIndex;
                _newestStarted = Math.Max(_newestStarted, turnIndex);
            }

            return slot;
        }

        /// <summary>Takes the turn slot, unless a turn of this conversation is still running.</summary>
        /// <returns><see langword="false"/> when another turn holds the slot.</returns>
        /// <exception cref="ObjectDisposedException">The conversation was disposed.</exception>
        internal bool TryEnterTurn()
        {
            lock (_session.TurnLock)
            {
                if (_closed)
                {
                    throw new ObjectDisposedException(
                        nameof(ConversationSession), $"The conversation '{_session.ConversationId}' was disposed, so it runs no further turn.");
                }

                if (_running)
                {
                    return false;
                }

                _running = true;
                return true;
            }
        }

        /// <summary>
        /// Refuses every later turn, and waits for the one that holds the slot, if any, to free it: a turn admitted
        /// before may still swap the session in its catch-up and start children on it.
        /// </summary>
        /// <returns>Completes once no turn holds the slot. A run the caller started and never read nor disposed holds it for good.</returns>
        internal Task CloseTurnsAsync()
        {
            lock (_session.TurnLock)
            {
                _closed = true;
                return _running ? FreedLocked() : Task.CompletedTask;
            }
        }

        /// <summary>Reads whether a turn holds the slot, without refusing any later turn.</summary>
        /// <returns>What completes once the running turn frees the slot, or <see langword="null"/> when no turn holds it.</returns>
        internal Task? RunningTurn()
        {
            lock (_session.TurnLock)
            {
                return _running ? FreedLocked() : null;
            }
        }

        private Task FreedLocked()
        {
            return (_freed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        /// <summary>Frees the turn slot after a turn refused to start left it taken.</summary>
        internal void ReleaseTurn()
        {
            TaskCompletionSource? freed;
            lock (_session.TurnLock)
            {
                _running = false;
                freed = _freed;
                _freed = null;
            }

            _ = freed?.TrySetResult();
        }

        /// <summary>
        /// Closes the window, and frees the session for the next turn.
        /// </summary>
        /// <param name="slot">The slot <see cref="StartRun"/> returned.</param>
        internal void EndRun(TurnCutSlot slot)
        {
            // The field drops first. A cut that arrives now meets no slot to cancel, so it reaches
            // the amendment path rather than a disposed one.
            lock (_session.TurnLock)
            {
                _slot = null;
            }

            slot.Dispose();
            ReleaseTurn();
        }

        /// <summary>
        /// Rewrites the reply of the turn that committed last to what the user saw or heard. Runs under the turn lock.
        /// </summary>
        /// <param name="turnIndex">The turn the cut names. Only the last committed turn is amended.</param>
        /// <param name="cut">What reached the user.</param>
        /// <param name="recut">Whether the cut replaces one the turn already took, rather than cutting it for the first time.</param>
        /// <returns>
        /// <see langword="true"/> when the turn was amended, and <see langword="false"/> when the turn is not the last
        /// one, was already cut (or, for a recut, never was), or wrote no reply.
        /// </returns>
        internal bool AmendTurn(int turnIndex, TurnCut cut, bool recut)
        {
            // The chain has already closed, so nothing may be appended behind conversation.ended.
            if (_session.Events.HasEnded
                || _session.AmendableEventId is not { } completedEventId
                || _session.LastTurn is not { } finished
                || (finished.Cut is not null) != recut
                || finished.TurnIndex != turnIndex
                || _session.Ledger.Session() is not { } session)
            {
                return false;
            }

            string shown = (cut.ShownText ?? _session.AmendableText ?? finished.ReplyText).Trim();

            if (!_session.History.RewriteReply(session, turnIndex, shown))
            {
                return false;
            }

            _session.LastTurn = finished with { ReplyText = shown, Cut = cut.Played ?? TimeSpan.Zero };
            _session.AmendableText = shown;

            _session.Events.RaiseReplyInterrupted(
                finished.TurnIndex, _session.Time.GetUtcNow(), completedEventId, shown, cut.Played);

            return true;
        }
    }
}

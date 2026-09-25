using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// Reads what store 1 holds beyond a session's words, and counts the session's writes the store did not take, so a
    /// catch-up knows whether another session wrote in their place.
    /// </summary>
    internal sealed class ConversationStoreReads : ITranscriptLossCounter
    {
        private readonly ConversationSession _session;

        private readonly TranscriptLosses _losses = new();

        internal ConversationStoreReads(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <summary>
        /// Gets whether the store lost a write of this session since it last caught up: it refused a turn, or the write
        /// failed. The session's words then no longer match the store, so its next turn reads the store back first.
        /// </summary>
        internal bool Diverged => _losses.Any;

        /// <summary>
        /// Gets whether the store refused a turn of this session because another session of the conversation saved that
        /// turn first, and the session has not caught up since. Such a session is never filed for a later request to
        /// resume: the ids already name the session whose turn the store kept. A write that merely failed is not a
        /// refusal: nothing else holds its ids, and store 0 outranks a filed session on resume.
        /// </summary>
        internal bool Refused => _losses.Refused;

        /// <summary>Records that the store refused a turn this session committed.</summary>
        internal void MarkRefused()
        {
            _losses.Lose([], refused: true);
        }

        /// <inheritdoc />
        public void Dropped(int turnIndex, IReadOnlyList<string> appended, Exception exception)
        {
            _losses.Lose(appended, exception is ConversationTurnConflictException);
            _session.Events.RaiseDroppedTranscriptWrite(turnIndex, exception);
        }

        /// <summary>Records that the session took what <paramref name="catchUp"/> read, and every loss counted before that read.</summary>
        internal void CaughtUp(TranscriptCatchUp catchUp)
        {
            _losses.CaughtUp(catchUp.LossMark);
        }

        /// <inheritdoc />
        public void Withdrew(IReadOnlyList<string> messageIds)
        {
            _losses.Withdrew(messageIds);
        }

        /// <inheritdoc />
        public async ValueTask<TranscriptLossVerdict?> JudgeAsync(AgentSession session, CancellationToken cancellationToken)
        {
            return Diverged && await ReadAsync(session, cancellationToken).ConfigureAwait(false) is { } read
                ? new TranscriptLossVerdict(read.LossMark, read.Overtaken)
                : null;
        }

        /// <inheritdoc />
        public void Realigned(TranscriptLossVerdict verdict)
        {
            _losses.Realigned(verdict);
        }

        /// <summary>
        /// Reads store when someone else wrote to the conversation since the session's words were last in line with
        /// it, or always once the store lost one of this session's writes. Changes nothing.
        /// </summary>
        /// <param name="opened">The session of this conversation.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>What store 1 holds, or <see langword="null"/> when it holds nothing the session lacks, or cannot be read.</returns>
        internal async ValueTask<TranscriptCatchUp?> ReadAsync(AgentSession opened, CancellationToken cancellationToken)
        {
            TranscriptPosition position = _session.History.Position(opened);
            await position.Written.ConfigureAwait(false);

            // Read once the writes above landed or dropped, so every loss among them is counted.
            (int mark, IReadOnlyList<TranscriptLoss> since) = _losses.Read();

            try
            {
                ConversationRecord refreshed = await RecordAsync(cancellationToken).ConfigureAwait(false);

                // A lost turn's words took the same ordinals another session's saved turn did, so the counters can
                // match while the words differ.
                if (since.Count == 0 && refreshed.NextOrdinal == position.NextOrdinal)
                {
                    return null;
                }

                IReadOnlyList<ConversationMessage> rows = await _session.Compiled.ConversationStore.ReadForSessionAsync(
                    _session.ConversationId, cancellationToken).ConfigureAwait(false);

                return new TranscriptCatchUp(position.Revision, rows, refreshed.NextOrdinal, refreshed.State)
                {
                    LossMark = mark,
                    Overtaken = since.Count > 0
                        && await OvertakenAsync(since, rows, refreshed.NextOrdinal, position.NextOrdinal, cancellationToken).ConfigureAwait(false),
                };
            }
#pragma warning disable CA1031 // A store that cannot be read never ends a conversation: the turn runs on the words the session holds.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                Log.TranscriptResyncFailed(_session.Logger, _session.ConversationId, _session.State.TurnIndex, exception);
                _session.Events.RaiseFailedTranscriptResync(_session.State.TurnIndex, exception);
                return null;
            }
        }

        /// <summary>Whether another session's writes stand in the store in place of this session's lost ones.</summary>
        /// <param name="since">The losses since the last catch-up.</param>
        /// <param name="rows">What store 1 read for the session.</param>
        /// <param name="stored">The next ordinal by store 0's own counter.</param>
        /// <param name="own">The next ordinal as the session counted it.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        private async ValueTask<bool> OvertakenAsync(
            IReadOnlyList<TranscriptLoss> since, IReadOnlyList<ConversationMessage> rows, int stored, int own, CancellationToken cancellationToken)
        {
            if (since.Any(static loss => loss.Refused || loss.Overtaken))
            {
                return true;
            }

            HashSet<string> read = new(rows.Select(static row => row.MessageId), StringComparer.Ordinal);
            int unlanded = 0;
            HashSet<int> untold = [0];

            foreach (TranscriptLoss loss in since)
            {
                if (loss.Appended.Count > 0 && loss.Held.Count == 0)
                {
                    untold.UnionWith([.. untold.Select(rowsUntold => rowsUntold + loss.Appended.Count)]);
                }
                else if (!await LandedAsync(loss.Held, read, cancellationToken).ConfigureAwait(false))
                {
                    unlanded += loss.Appended.Count;
                }
            }

            return !untold.Contains(own - unlanded - stored);
        }

        /// <summary>Whether the store holds every one of these rows, including a row a newer summary now stands in for.</summary>
        private async ValueTask<bool> LandedAsync(IReadOnlyList<string> held, HashSet<string> read, CancellationToken cancellationToken)
        {
            foreach (string messageId in held)
            {
                if (!read.Contains(messageId)
                    && await _session.Compiled.ConversationStore.OrdinalOfAsync(_session.ConversationId, messageId, cancellationToken).ConfigureAwait(false) is null)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Store 0's record of this conversation, which must exist for a session that already opened it.</summary>
        /// <param name="cancellationToken">Cancels the read.</param>
        private async ValueTask<ConversationRecord> RecordAsync(CancellationToken cancellationToken)
        {
            return await _session.Compiled.ConversationStore.GetAsync(_session.ConversationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Store 0 holds no conversation '{_session.ConversationId}' for its own session to resync against.");
        }
    }
}

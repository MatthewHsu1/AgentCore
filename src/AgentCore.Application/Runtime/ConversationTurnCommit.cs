using AgentCore.Application.Diagnostics;
using AgentCore.Domain;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The publish half of a turn: writes the turn's events once its words are committed, and tears the
    /// conversation down when it is over. <see cref="ConversationTurnCompletion"/> decides the turn; this publishes it.
    /// </summary>
    internal sealed class ConversationTurnCommit
    {
        /// <summary>The failure a refused turn's span ends with.</summary>
        internal const string RefusedReason =
            "the store refused the turn's words: another session of the conversation saved the same turn first.";

        private readonly ConversationSession _session;

        internal ConversationTurnCommit(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <summary>
        /// Waits for the store's answer to a turn's append, however long it takes, and marks the session refused when
        /// the store refused it.
        /// </summary>
        /// <param name="refused">Completes with <see langword="true"/> when the store refused the append. It never faults.</param>
        /// <returns><see langword="true"/> when the store refused the turn.</returns>
        internal async ValueTask<bool> RefusedAsync(Task<bool> refused)
        {
            if (!await refused.ConfigureAwait(false))
            {
                return false;
            }

            _session.Ledger.Reads.MarkRefused();
            return true;
        }

        /// <summary>Publishes one committed turn. Runs under the turn lock, right after the commit.</summary>
        /// <param name="ids">What the commit wrote the user's message and the last message under, or <see langword="null"/>.</param>
        /// <param name="spoken">The words the turn's rows say. An uncut turn's <c>turn.completed</c> and a cut turn's
        /// <c>reply.interrupted</c> hash them, so the verify query rebuilds exactly what the chain proves.</param>
        /// <param name="outcome">What the turn said and what the caller heard.</param>
        /// <param name="result">The finished turn.</param>
        /// <param name="cut">The cut the turn was sealed with, or <see langword="null"/>.</param>
        /// <param name="late">
        /// A barge-in that reached the turn after its seal, to amend the reply just written, or <see langword="null"/>.
        /// When <paramref name="cut"/> is set, it is a recut that replaces that cut.
        /// </param>
        /// <returns>The turn as published, amended when <paramref name="late"/> landed.</returns>
        internal TurnResult Publish(
            (string UserMessageId, string? ReplyMessageId)? ids,
            string spoken,
            ReplyOutcome outcome,
            TurnResult result,
            TurnCut? cut,
            TurnCut? late)
        {
            _session.LastReplyMessageId = ids is { } written ? written.ReplyMessageId ?? written.UserMessageId : null;

            if (outcome.Fault is { } fault && outcome.IsToolFault)
            {
                Log.ToolBudgetSpent(_session.Logger, result.ConversationId, result.TurnIndex, fault);
            }

            // A cut turn's rows hold what was shown, so its turn.completed proves what the model produced instead.
            Guid completedEventId = _session.Events.WriteTurnEvents(
                result, cut is null ? spoken : outcome.GeneratedText, spoken, outcome.ToolFault, cut?.Played);

            _session.AmendableEventId = completedEventId;
            _session.AmendableText = spoken;

            _session.LastTurn = result;

            if (late is not null
                && _session.Cuts.AmendTurn(result.TurnIndex, late, recut: cut is not null)
                && _session.LastTurn is { } amended)
            {
                result = amended;
            }

            return result;
        }

        /// <summary>Closes the chain of a conversation the turn left in a terminal stage, and releases what it held.</summary>
        /// <param name="stageAfter">The stage the turn left the conversation in.</param>
        internal async Task EndConversationIfOverAsync(string stageAfter)
        {
            if (_session.IsComplete)
            {
                _ = _session.Events.EndConversation(ConversationEndReason.AgentCompleted, _session.Time.GetUtcNow(), stageAfter);
            }

            if (!_session.IsComplete && !_session.Events.HasEnded)
            {
                return;
            }

            await _session.Lifetime.ReleaseBackgroundSessionsAsync().ConfigureAwait(false);

            await _session.Lifetime.DisposeShellsAsync().ConfigureAwait(false);

            _session.Lifetime.DeleteWorkspace();
        }
    }
}

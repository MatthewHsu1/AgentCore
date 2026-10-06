using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Domain;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Runtime.Turn.Lifecycle
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

        /// <summary>
        /// Publishes one committed turn. Runs under the turn lock, right after the commit, so a turn the store refused
        /// names no stage move.
        /// </summary>
        /// <param name="ids">What the commit wrote the user's message and the last message under, or <see langword="null"/>.</param>
        /// <param name="spoken">The words the turn's rows say.</param>
        /// <param name="result">The finished turn.</param>
        /// <param name="cut">The cut the turn was sealed with, or <see langword="null"/>.</param>
        /// <param name="late">
        /// A barge-in that reached the turn after its seal, to amend the reply just written, or <see langword="null"/>.
        /// When <paramref name="cut"/> is set, it is a recut that replaces that cut.
        /// </param>
        /// <param name="completed">The turn's notice, raised after the turn's <see cref="StageChanged"/>, if it moved the stage.</param>
        /// <returns>The turn as published, amended when <paramref name="late"/> landed.</returns>
        internal TurnResult Publish(
            (string UserMessageId, string? ReplyMessageId)? ids,
            string spoken,
            TurnResult result,
            TurnCut? cut,
            TurnCut? late,
            TurnCompleted completed)
        {
            _session.LastReplyMessageId = ids is { } written ? written.ReplyMessageId ?? written.UserMessageId : null;

            SessionHooks hooks = _session.Hooks;
            if (!string.Equals(result.StageAfter, result.StageBefore, StringComparison.Ordinal))
            {
                _ = hooks.Raise(new StageChanged(hooks.Scope(result.TurnIndex, result.StageBefore), result.StageBefore, result.StageAfter), result.EndedAt);
            }

            _session.AmendableEventId = hooks.Raise(completed, result.EndedAt);
            if (cut is not null)
            {
                RaiseCut(result.TurnIndex, spoken, cut.Played, result.EndedAt);
            }

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

        /// <summary>Raises the cut that amends the turn's <see cref="TurnCompleted"/>.</summary>
        /// <param name="turnIndex">The cut turn.</param>
        /// <param name="heard">The words the caller heard.</param>
        /// <param name="played">How long the reply played, or <see langword="null"/>.</param>
        /// <param name="occurredAt">The turn's end for a cut sealed with the turn, or <see langword="null"/> for a later cut: now.</param>
        internal void RaiseCut(int turnIndex, string heard, TimeSpan? played, DateTimeOffset? occurredAt = null)
        {
            SessionHooks hooks = _session.Hooks;
            _ = hooks.Raise(new ReplyCut(hooks.Scope(turnIndex, stage: null), heard, played, _session.AmendableEventId ?? Guid.Empty), occurredAt);
        }

        /// <summary>
        /// Raises the end a turn left pending or reached, including that of a conversation the turn left in a terminal
        /// stage, and releases what it held.
        /// </summary>
        /// <param name="stageAfter">The stage the turn left the conversation in.</param>
        /// <param name="turnIndex">The turn that just sealed.</param>
        internal async Task EndConversationIfOverAsync(string stageAfter, int turnIndex)
        {
            _session.Lifetime.Ending.AfterTurn(turnIndex, stageAfter, _session.IsComplete);

            if (!_session.IsComplete && !_session.Lifetime.Ending.Requested)
            {
                return;
            }

            // A terminal stage ends the conversation as a host end does, so a tool a cut turn left running gets the same grace.
            _session.Lifetime.Ending.StopToolsAfterGrace();

            await _session.Lifetime.ReleaseBackgroundSessionsAsync().ConfigureAwait(false);

            await _session.Lifetime.Cleanup.AfterToolsAsync(shells: true).ConfigureAwait(false);
        }
    }
}

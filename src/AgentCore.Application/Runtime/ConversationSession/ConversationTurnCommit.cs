using AgentCore.Domain;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The publish half of a turn: appends the transcript, writes the turn's events, and tears the
/// conversation down when it is over. <see cref="ConversationTurnCompletion"/> decides the turn; this commits it.
/// </summary>
internal sealed class ConversationTurnCommit
{
    private readonly ConversationSession _session;

    internal ConversationTurnCommit(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    internal TurnResult Publish(
        ConversationTurn turn,
        ReplyOutcome outcome,
        TranscriptEntry addition,
        TurnResult result,
        ConversationInterruptionTracker.Interruption? interruption)
    {
        lock (_session.InterruptLock)
        {
            if (_session.SessionCarriesHistory)
            {
                _session.LastReplyMessageId = _session.History.AppendTurn(
                    turn.Session, [turn.Spoken, .. addition.Written], _session.Snapshot(), turn.MessageId);
            }
            else
            {
                _session.LastReplyMessageId = _session.History.AppendCallerFacingTurn(
                    turn.Session, turn.Spoken, addition.Heard, _session.Snapshot(), turn.MessageId);
            }

            var completedEventId = _session.Events.WriteTurnEvents(result, outcome.SpokenReply, outcome.ToolFault);

            _session.AmendableEventId = outcome.InterruptedAfter is null ? completedEventId : null;

            _session.LastTurn = result;

            if (interruption is null
                && _session.Interruption is { } late
                && _session.Interruptions.AmendLastTurn(late)
                && _session.LastTurn is { } amended)
            {
                result = amended;
            }

            _session.Interruption = null;

            _session.RunCancellation = null;

            _session.Interruptions.RunIsAudible = false;
        }

        return result;
    }

    internal async Task EndConversationIfOverAsync(DateTimeOffset endedAt, string stageAfter)
    {
        if (_session.IsComplete)
        {
            _session.Events.EndConversation(ConversationEndReason.AgentCompleted, endedAt, stageAfter);
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

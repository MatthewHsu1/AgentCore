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

    // One lock, one moment. A late barge-in reads all four of these together, so a window in
    // which LastTurn already names this turn while the span or the ordinal still names the one
    // before it would let an amendment rewrite the wrong part of the transcript.
    internal TurnResult Publish(ConversationTurn turn, ReplyOutcome outcome, TranscriptEntry addition, TurnResult result, ConversationInterruptionTracker.Interruption? interruption)
    {
        lock (_session.InterruptLock)
        {
            // What the caller said and what the caller heard, written together.
            if (_session.SessionCarriesHistory)
            {
                _session.LastReplyMessageId = _session.History.AppendTurn(
                    turn.Session, [turn.Spoken, .. addition.Written], _session.Snapshot(), turn.MessageId);
            }
            else
            {
                // Rows 3 and 4 answer with one reply per node, and the caller hears one of them.
                // Store 1 holds the caller-facing turn alone, so the deliberation that produced the
                // answer never enters the record and never reaches a later turn.
                _session.LastReplyMessageId = _session.History.AppendCallerFacingTurn(
                    turn.Session, turn.Spoken, addition.Heard, _session.Snapshot(), turn.MessageId);
            }

            // Only now. The chain stores a hash of the spoken text and store 1 stores the text, so a
            // reply.interrupted raised before the append would name a hash of words nothing holds.
            var completedEventId = _session.Events.WriteTurnEvents(result, outcome.SpokenReply, outcome.ToolFault);

            // A turn one barge-in already cut is not amendable again, so it is not published here.
            _session.AmendableEventId = outcome.InterruptedAfter is null ? completedEventId : null;
            _session.LastTurn = result;

            // The second read. The first one happened before the extractor await, and that await is
            // bounded only by TurnCompletionTimeout, so a barge-in has five whole seconds in which
            // to land after this turn already decided it was not interrupted. On a streaming turn it
            // finds _runCancellation still set and the run still audible, because EndRun does not
            // run until this method has returned, so it takes the in-flight path, cancels a run that
            // has already stopped, and answers true — and true, on the contract of Interrupt, means
            // the barge-in is recorded. (A turn that never streamed was never audible, so its frame
            // already took the amendment path against the turn before this one and never lands
            // here.) Nothing recorded the streaming frame. Reading _interruption again here, one
            // statement after _amendable and LastTurn were published, hands that frame to the very
            // path a frame arriving one instant later would have taken: the tested amendment, which
            // writes the TurnCompleted then ReplyInterrupted pair T23 asks for. The _amendableEventId
            // guard above already fits: it is published exactly when interruptedAfter is null, which
            // is the only state this window can be reached in.
            if (interruption is null
                && _session.Interruption is { } late
                && _session.Interruptions.AmendLastTurn(late)
                && _session.LastTurn is { } amended)
            {
                // The amendment republished LastTurn, so the turn this method returns and the
                // duration the telemetry reads must both come from it and not from the record
                // built before the frame landed.
                result = amended;
            }

            // Consumed, on whichever path recorded it: the in-flight read at the top of the turn
            // fed WriteTurnEvents, and the amendment above wrote its own pair. Nothing downstream
            // may handle one frame twice.
            _session.Interruption = null;

            // The turn is committed, so the run is no longer what the caller is hearing. This is the
            // tail of the same defect the read above fixes: a barge-in landing between this lock and
            // EndRun would otherwise still find the run audible, take the in-flight path, and be
            // recorded nowhere at all. Cleared here, under the lock that just published LastTurn, it
            // reaches AmendLastTurn against this turn instead. EndRun still runs and still owns
            // disposing the source; clearing the field twice costs nothing.
            _session.RunCancellation = null;
            _session.Interruptions.RunIsAudible = false;
        }

        return result;
    }

    internal async Task EndConversationIfOverAsync(DateTimeOffset endedAt, string stageAfter)
    {
        if (_session.IsComplete)
        {
            // The machine reached a terminal stage, so the conversation is over and the chain closes here.
            // The stage rides as detail, because the reason a report counts is the same one for
            // every terminal stage the document declares.
            _session.Events.EndConversation(ConversationEndReason.AgentCompleted, endedAt, stageAfter);
        }

        if (!_session.IsComplete && !_session.Events.HasEnded)
        {
            return;
        }

        // Children before shells: a cancelled child may be inside a shell tool the next line
        // tears down.
        await _session.Lifetime.ReleaseBackgroundSessionsAsync().ConfigureAwait(false);

        // Shells first: a Docker executor's bind mount is this workspace, and it must not be
        // deleted out from under a still-live container.
        await _session.Lifetime.DisposeShellsAsync().ConfigureAwait(false);

        // A tool running mid-turn (a file_memory_write, say) can call EndConversation itself, which
        // deletes the workspace and then IsComplete overwrites the flag when the policy also
        // reaches a terminal stage. _events.HasEnded survives that overwrite, so the folder a
        // tool recreated after the mid-turn delete is still cleaned up here.
        _session.Lifetime.DeleteWorkspace();
    }
}

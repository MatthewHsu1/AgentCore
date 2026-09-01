using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Diagnostics;

/// <summary>
/// Counts the facts of a call on the three instruments of section 8.6.
/// </summary>
internal sealed class TelemetryCallObserver : ICallObserver
{
    /// <inheritdoc />
    public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callEvent);

        switch (callEvent.Kind)
        {
            case CallEventKind.PromptFlagged:
                AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationFlagged);
                break;

            case CallEventKind.ModerationUnavailable:
                // The turn ran unchecked, because moderation fails open. This value is the only
                // record of that, so an operator alerts on it rather than on a log line.
                AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationUnavailable);
                break;

            case CallEventKind.ModerationClean:
                AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationClean);
                break;

            case CallEventKind.ToolFailed:
                AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureTool);
                break;

            case CallEventKind.EmptyReply:
                AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureEmptyReply);
                break;

            case CallEventKind.ExtractionFailed:
                AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureExtraction);
                break;

            default:
                // Six kinds reach here. Four of them — CallStarted, TurnCompleted, ReplyInterrupted
                // and CallEnded — are counted below, as rows of the chain, and nowhere else.
                //
                // The other two, TranscriptWriteFailed and StateRestorePartial, are counted NOWHERE.
                // Neither has an instrument that would take it: agentcore.turn.failures counts what a
                // TURN failed at, by a closed set of values T61 keeps closed because a new value costs
                // a permanent series. A refused store 1 write is a fault of the system rather than of
                // the turn, and a dropped slot happens before any turn has run, so both would need an
                // instrument of their own and not a fourth value on that one. They are reported as log
                // lines instead, and saying so here is cheaper than an operator reading this switch and
                // assuming a counter exists.
                break;
        }

        // Section 8.6 counts the events the turn loop handed to the sink, by kind, and it counted them
        // whatever the sink then did with them: the old call sat at the top of CallSession.Append,
        // above the enqueue and above the try. A kind the chain does not store is not one of them, so
        // the six diagnostic kinds are counted by their failure or their verdict above, or not at all,
        // and never here.
        if (CallEventKinds.TryGetAuditKind(callEvent.Kind, out AuditEventKind auditKind))
        {
            AgentCoreTelemetry.RecordAuditEvent(AuditEventKinds.ToToken(auditKind));
        }

        return ValueTask.CompletedTask;
    }
}

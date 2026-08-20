using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Diagnostics;

/// <summary>
/// Counts the facts of a call on the three instruments of section 8.6.
/// </summary>
/// <remarks>
/// <para>
/// Every counter <see cref="CallSession"/> used to touch itself is touched here instead, and nothing
/// about the numbers changed: the same instrument, the same attribute key, and the same closed set of
/// values. T61 is why the values are closed, and it costs money to break — a call id on a metric
/// attribute is one permanent series for each call.
/// </para>
/// <para>
/// The span of a turn is NOT here. <see cref="AgentCoreTelemetry.StartTurn"/> and
/// <see cref="AgentCoreTelemetry.EndTurn"/> bracket a running turn and the <c>Activity</c> travels
/// with it, so they stay in the session, where the two ends of the turn are. This observer reads facts
/// that have already happened, and a counter is exactly that.
/// </para>
/// <para>
/// It never fails and never waits, so it always completes synchronously and the dispatcher's fast path
/// costs the turn the counter and nothing else. One instance serves every call.
/// </para>
/// </remarks>
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
                // The four remaining kinds are counted below and nowhere else.
                break;
        }

        // Section 8.6 counts the events the turn loop handed to the sink, by kind, and it counted them
        // whatever the sink then did with them: the old call sat at the top of CallSession.Append,
        // above the enqueue and above the try. A kind the chain does not store is not one of them, so
        // the four diagnostic kinds are counted by their failure or their verdict above, and never
        // here.
        if (CallEventKinds.TryGetAuditKind(callEvent.Kind, out AuditEventKind auditKind))
        {
            AgentCoreTelemetry.RecordAuditEvent(AuditEventKinds.ToToken(auditKind));
        }

        return ValueTask.CompletedTask;
    }
}

using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Diagnostics
{
    /// <summary>
    /// Counts the facts of a conversation on the three instruments of section 8.6.
    /// </summary>
    internal sealed class TelemetryConversationObserver : IConversationObserver
    {
        /// <inheritdoc />
        public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(conversationEvent);

            switch (conversationEvent.Kind)
            {
                case ConversationEventKind.PromptFlagged:
                    AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationFlagged);
                    break;

                case ConversationEventKind.ModerationUnavailable:
                    // The turn ran unchecked, because moderation fails open. This value is the only
                    // record of that, so an operator alerts on it rather than on a log line.
                    AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationUnavailable);
                    break;

                case ConversationEventKind.ModerationClean:
                    AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationClean);
                    break;

                case ConversationEventKind.ToolFailed:
                    AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureTool);
                    break;

                case ConversationEventKind.EmptyReply:
                    AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureEmptyReply);
                    break;

                case ConversationEventKind.ExtractionFailed:
                    AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureExtraction);
                    break;

                case ConversationEventKind.ConversationStarted:
                case ConversationEventKind.TurnCompleted:
                case ConversationEventKind.ReplyInterrupted:
                case ConversationEventKind.TurnSuperseded:
                case ConversationEventKind.ConversationEnded:
                    // Counted below, as rows of the chain, and nowhere else.
                    break;

                case ConversationEventKind.TranscriptWriteFailed:
                case ConversationEventKind.StateRestorePartial:
                case ConversationEventKind.TranscriptResyncFailed:
                    // Counted NOWHERE. No instrument would take them: agentcore.turn.failures counts what
                    // a TURN failed at, by a closed set of values T61 keeps closed because a new value costs
                    // a permanent series. A refused store 1 write is a fault of the system rather than of
                    // the turn, and a dropped slot happens before any turn has run, so each would need an
                    // instrument of its own and not another value on that one. They are reported as log
                    // lines instead, and saying so here is cheaper than an operator reading this switch and
                    // assuming a counter exists.
                    break;

                default:
                    break;
            }

            // Section 8.6 counts the events the turn loop handed to the sink, by kind, and it counted them
            // whatever the sink then did with them: the old conversation sat at the top of ConversationSession.Append,
            // above the enqueue and above the try. A kind the chain does not store is not one of them, so
            // the six diagnostic kinds are counted by their failure or their verdict above, or not at all,
            // and never here.
            if (ConversationEventKinds.TryGetAuditKind(conversationEvent.Kind, out AuditEventKind auditKind))
            {
                AgentCoreTelemetry.RecordAuditEvent(AuditEventKinds.ToToken(auditKind));
            }

            return ValueTask.CompletedTask;
        }
    }
}

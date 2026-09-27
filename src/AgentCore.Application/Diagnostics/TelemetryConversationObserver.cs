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
                    AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationUnavailable);
                    break;

                case ConversationEventKind.ModerationClean:
                    AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationClean);
                    break;

                case ConversationEventKind.ToolFailed:
                    if (!conversationEvent.Payload.ContainsKey(AuditPayloadKeys.ToolCallId))
                    {
                        AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureTool);
                    }

                    break;

                case ConversationEventKind.EmptyReply:
                    AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureEmptyReply);
                    break;

                case ConversationEventKind.RunFaulted:
                    AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureRun);
                    break;

                case ConversationEventKind.ExtractionFailed:
                    AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureExtraction);
                    break;

                case ConversationEventKind.ConversationStarted:
                case ConversationEventKind.TurnCompleted:
                case ConversationEventKind.ReplyInterrupted:
                case ConversationEventKind.TurnSuperseded:
                case ConversationEventKind.TurnRefused:
                case ConversationEventKind.ConversationEnded:
                    break;

                case ConversationEventKind.TranscriptWriteFailed:
                case ConversationEventKind.StateRestorePartial:
                case ConversationEventKind.TranscriptResyncFailed:
                    break;

                default:
                    break;
            }

            if (ConversationEventKinds.TryGetAuditKind(conversationEvent.Kind, out AuditEventKind auditKind))
            {
                AgentCoreTelemetry.RecordAuditEvent(AuditEventKinds.ToToken(auditKind));
            }

            return ValueTask.CompletedTask;
        }
    }
}

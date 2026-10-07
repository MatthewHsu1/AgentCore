using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Hooks.BuiltIn
{
    /// <summary>
    /// Today's counters, read from notices: failures, moderation verdicts, and the audit rows the chain writes.
    /// Spans stay inline: an OTel activity must flow with the call.
    /// </summary>
    internal sealed class TelemetryHook : AgentHook
    {
        /// <inheritdoc />
        public override ValueTask OnConversationStartedAsync(ConversationStarted notice, CancellationToken cancellationToken)
        {
            return Audited(AuditEventKind.ConversationStarted);
        }

        /// <inheritdoc />
        public override ValueTask OnConversationEndedAsync(ConversationEnded notice, CancellationToken cancellationToken)
        {
            return Audited(AuditEventKind.ConversationEnded);
        }

        /// <inheritdoc />
        public override ValueTask OnTurnRefusedAsync(TurnRefused notice, CancellationToken cancellationToken)
        {
            return AuditRules.IsAudited(notice) ? Audited(AuditEventKind.TurnRefused) : ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override ValueTask OnTurnSupersededAsync(TurnSuperseded notice, CancellationToken cancellationToken)
        {
            return Audited(AuditEventKind.TurnSuperseded);
        }

        /// <inheritdoc />
        public override ValueTask OnReplyCutAsync(ReplyCut notice, CancellationToken cancellationToken)
        {
            return Audited(AuditEventKind.ReplyInterrupted);
        }

        /// <inheritdoc />
        public override ValueTask OnInputModeratedAsync(InputModerated notice, CancellationToken cancellationToken)
        {
            AgentCoreTelemetry.RecordModeration(notice.Verdict switch
            {
                InputVerdict.Flagged => AgentCoreTelemetry.ModerationFlagged,
                InputVerdict.Unavailable => AgentCoreTelemetry.ModerationUnavailable,
                InputVerdict.Clean => AgentCoreTelemetry.ModerationClean,
                _ => throw new ArgumentOutOfRangeException(nameof(notice), notice.Verdict, "The verdict is outside the closed set."),
            });

            return AuditRules.IsAudited(notice) ? Audited(AuditEventKind.PromptFlagged) : ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override ValueTask OnToolCalledAsync(ToolCalled notice, CancellationToken cancellationToken)
        {
            return AuditRules.IsAudited(notice) ? Audited(AuditEventKind.ToolFailed) : ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken)
        {
            TurnFailureAccounting.Count(TurnFailureAccounting.Of(notice.Outcome, notice.FailedInTool));

            return Audited(AuditEventKind.TurnCompleted);
        }

        /// <inheritdoc />
        public override ValueTask OnFaultAsync(Fault notice, CancellationToken cancellationToken)
        {
            if (notice.Kind == FaultKind.ExtractionFailed)
            {
                AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureExtraction);
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override ValueTask OnTurnLatencyAsync(TurnLatency notice, CancellationToken cancellationToken)
        {
            switch (notice.Metric)
            {
                case LatencyMetric.TimeToFirstToken:
                    AgentCoreTelemetry.RecordTimeToFirstToken(notice.Value);
                    break;
                case LatencyMetric.TimeToFirstSpeech:
                    AgentCoreTelemetry.RecordTimeToFirstSpeech(notice.Value);
                    break;
                case LatencyMetric.TimeToReplyEnd:
                    AgentCoreTelemetry.RecordTimeToReplyEnd(notice.Value);
                    break;
                case LatencyMetric.BargeIn:
                    AgentCoreTelemetry.RecordBargeInLatency(notice.Value);
                    break;
                default:
                    break;
            }

            return ValueTask.CompletedTask;
        }

        private static ValueTask Audited(AuditEventKind kind)
        {
            AgentCoreTelemetry.RecordAuditEvent(AuditEventKinds.ToToken(kind));
            return ValueTask.CompletedTask;
        }
    }
}

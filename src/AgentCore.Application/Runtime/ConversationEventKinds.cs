using System.Collections.Frozen;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// What one <see cref="ConversationEventKind"/> is to the audit vocabulary, in one place.
    /// </summary>
    internal static class ConversationEventKinds
    {
        /// <summary>The seven kinds the chain stores, and the audit kind each one is stored as.</summary>
        private static readonly FrozenDictionary<ConversationEventKind, AuditEventKind> AuditKinds =
            new Dictionary<ConversationEventKind, AuditEventKind>
            {
                [ConversationEventKind.ConversationStarted] = AuditEventKind.ConversationStarted,
                [ConversationEventKind.PromptFlagged] = AuditEventKind.PromptFlagged,
                [ConversationEventKind.ToolFailed] = AuditEventKind.ToolFailed,
                [ConversationEventKind.TurnCompleted] = AuditEventKind.TurnCompleted,
                [ConversationEventKind.ReplyInterrupted] = AuditEventKind.ReplyInterrupted,
                [ConversationEventKind.ConversationEnded] = AuditEventKind.ConversationEnded,
                [ConversationEventKind.TurnSuperseded] = AuditEventKind.TurnSuperseded,
            }.ToFrozenDictionary();

        /// <summary>
        /// Reads the audit kind one conversation event maps to, when the chain stores it at all.
        /// </summary>
        public static bool TryGetAuditKind(ConversationEventKind kind, out AuditEventKind auditKind)
        {
            return AuditKinds.TryGetValue(kind, out auditKind);
        }

        /// <summary>
        /// Names one kind for a log line.
        /// </summary>
        public static string ToToken(ConversationEventKind kind)
        {
            return kind switch
            {
                ConversationEventKind.ConversationStarted => AuditEventKinds.ToToken(AuditEventKind.ConversationStarted),
                ConversationEventKind.PromptFlagged => AuditEventKinds.ToToken(AuditEventKind.PromptFlagged),
                ConversationEventKind.ToolFailed => AuditEventKinds.ToToken(AuditEventKind.ToolFailed),
                ConversationEventKind.TurnCompleted => AuditEventKinds.ToToken(AuditEventKind.TurnCompleted),
                ConversationEventKind.ReplyInterrupted => AuditEventKinds.ToToken(AuditEventKind.ReplyInterrupted),
                ConversationEventKind.ConversationEnded => AuditEventKinds.ToToken(AuditEventKind.ConversationEnded),
                ConversationEventKind.TurnSuperseded => AuditEventKinds.ToToken(AuditEventKind.TurnSuperseded),
                ConversationEventKind.ModerationUnavailable => "moderation.unavailable",
                ConversationEventKind.ModerationClean => "moderation.clean",
                ConversationEventKind.EmptyReply => "reply.empty",
                ConversationEventKind.ExtractionFailed => "extraction.failed",
                ConversationEventKind.TranscriptWriteFailed => "transcript.write.failed",
                ConversationEventKind.StateRestorePartial => "state.restore.partial",
                ConversationEventKind.TranscriptResyncFailed => "transcript.resync.failed",

                // A kind outside the closed set must not cost the report the fault it is carrying, so this
                // names the value instead of throwing over it.
                _ => kind.ToString(),
            };
        }
    }
}

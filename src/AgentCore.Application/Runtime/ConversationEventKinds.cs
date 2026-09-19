using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>
/// What one <see cref="ConversationEventKind"/> is to the audit vocabulary, in one place.
/// </summary>
internal static class ConversationEventKinds
{
    /// <summary>
    /// Reads the audit kind one conversation event maps to, when the chain stores it at all.
    /// </summary>
    public static bool TryGetAuditKind(ConversationEventKind kind, out AuditEventKind auditKind)
    {
        switch (kind)
        {
            case ConversationEventKind.ConversationStarted: auditKind = AuditEventKind.ConversationStarted; return true;
            case ConversationEventKind.PromptFlagged: auditKind = AuditEventKind.PromptFlagged; return true;
            case ConversationEventKind.ToolFailed: auditKind = AuditEventKind.ToolFailed; return true;
            case ConversationEventKind.TurnCompleted: auditKind = AuditEventKind.TurnCompleted; return true;
            case ConversationEventKind.ReplyInterrupted: auditKind = AuditEventKind.ReplyInterrupted; return true;
            case ConversationEventKind.ConversationEnded: auditKind = AuditEventKind.ConversationEnded; return true;
            case ConversationEventKind.TurnSuperseded: auditKind = AuditEventKind.TurnSuperseded; return true;
            default: auditKind = default; return false;
        }
    }

    /// <summary>
    /// Names one kind for a log line.
    /// </summary>
    public static string ToToken(ConversationEventKind kind)
    {
        if (TryGetAuditKind(kind, out AuditEventKind auditKind))
        {
            return AuditEventKinds.ToToken(auditKind);
        }

        return kind switch
        {
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

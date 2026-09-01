using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>
/// What one <see cref="CallEventKind"/> is to the audit vocabulary, in one place.
/// </summary>
internal static class CallEventKinds
{
    /// <summary>
    /// Reads the audit kind one call event maps to, when the chain stores it at all.
    /// </summary>
    public static bool TryGetAuditKind(CallEventKind kind, out AuditEventKind auditKind)
    {
        switch (kind)
        {
            case CallEventKind.CallStarted: auditKind = AuditEventKind.CallStarted; return true;
            case CallEventKind.PromptFlagged: auditKind = AuditEventKind.PromptFlagged; return true;
            case CallEventKind.ToolFailed: auditKind = AuditEventKind.ToolFailed; return true;
            case CallEventKind.TurnCompleted: auditKind = AuditEventKind.TurnCompleted; return true;
            case CallEventKind.ReplyInterrupted: auditKind = AuditEventKind.ReplyInterrupted; return true;
            case CallEventKind.CallEnded: auditKind = AuditEventKind.CallEnded; return true;
            default: auditKind = default; return false;
        }
    }

    /// <summary>
    /// Names one kind for a log line.
    /// </summary>
    public static string ToToken(CallEventKind kind)
    {
        if (TryGetAuditKind(kind, out AuditEventKind auditKind))
        {
            return AuditEventKinds.ToToken(auditKind);
        }

        return kind switch
        {
            CallEventKind.ModerationUnavailable => "moderation.unavailable",
            CallEventKind.ModerationClean => "moderation.clean",
            CallEventKind.EmptyReply => "reply.empty",
            CallEventKind.ExtractionFailed => "extraction.failed",
            CallEventKind.TranscriptWriteFailed => "transcript.write.failed",
            CallEventKind.StateRestorePartial => "state.restore.partial",

            // A kind outside the closed set must not cost the report the fault it is carrying, so this
            // names the value instead of throwing over it.
            _ => kind.ToString(),
        };
    }
}

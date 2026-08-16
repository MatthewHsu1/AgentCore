using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>
/// What one <see cref="CallEventKind"/> is to the audit vocabulary, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CallEventKind"/> is larger than <see cref="AuditEventKind"/>, and nothing casts between
/// them. Two components need to know which of the ten kinds the chain of D23 holds — the observer that
/// writes the row and the observer that counts it — and a mapping written twice is a mapping that
/// drifts. It is written here, once.
/// </para>
/// <para>
/// Nothing here allocates a token of its own for a stored kind. <see cref="AuditEventKinds"/> owns
/// every wire token the chain hashes, and it is a permanent promise (T56), so
/// <see cref="ToToken(CallEventKind)"/> asks it rather than repeating its strings.
/// </para>
/// </remarks>
internal static class CallEventKinds
{
    /// <summary>Reads the audit kind one call event maps to, when the chain stores it at all.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="auditKind">The kind the row carries, when there is a row.</param>
    /// <returns>
    /// <see langword="true"/> when the chain stores this kind, and <see langword="false"/> for a
    /// diagnostic-only kind that is counted and logged and stored nowhere.
    /// </returns>
    /// <remarks>
    /// A kind outside the closed set answers <see langword="false"/> rather than throwing. An observer
    /// records the call and is never a part of it, so an unknown value costs the chain a row and never
    /// costs the caller the turn.
    /// </remarks>
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

    /// <summary>Names one kind for a log line.</summary>
    /// <param name="kind">The kind of the fact to name.</param>
    /// <returns>The token the line reports.</returns>
    /// <remarks>
    /// The six kinds the chain stores answer with the token
    /// <see cref="AuditEventKinds.ToToken(AuditEventKind)"/> already produces, so the line an operator
    /// reads is unchanged from the one the turn loop used to write itself. The four diagnostic kinds
    /// reach no chain and therefore have no wire token of their own; they are named here in the same
    /// dotted form, for a log line and for nothing else. Nothing stores these strings, so none of them
    /// is a promise the way an audit token is.
    /// </remarks>
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

            // A kind outside the closed set must not cost the report the fault it is carrying, so this
            // names the value instead of throwing over it.
            _ => kind.ToString(),
        };
    }
}

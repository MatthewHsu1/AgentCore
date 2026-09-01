namespace AgentCore.Domain.Audit;

/// <summary>
/// The wire token of each <see cref="AuditEventKind"/>.
/// </summary>
public static class AuditEventKinds
{
    /// <summary>Reads the wire token of one kind.</summary>
    /// <param name="kind">The kind to name.</param>
    /// <returns>The token the canonical form writes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a member of the closed set.</exception>
    public static string ToToken(AuditEventKind kind) => kind switch
    {
        AuditEventKind.CallStarted => "call.started",
        AuditEventKind.TurnCompleted => "turn.completed",
        AuditEventKind.ReplyInterrupted => "reply.interrupted",
        AuditEventKind.ToolFailed => "tool.failed",
        AuditEventKind.CallEnded => "call.ended",
        AuditEventKind.PromptFlagged => "prompt.flagged",
        AuditEventKind.TurnSuperseded => "turn.superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The audit event vocabulary is closed, and this value is not in it."),
    };

    /// <summary>Reads the kind behind one wire token.</summary>
    /// <param name="token">The token a stored row holds.</param>
    /// <param name="kind">The kind, when the token is known.</param>
    /// <returns><see langword="true"/> when the token names a kind.</returns>
    public static bool TryParse(string? token, out AuditEventKind kind)
    {
        switch (token)
        {
            case "call.started": kind = AuditEventKind.CallStarted; return true;
            case "turn.completed": kind = AuditEventKind.TurnCompleted; return true;
            case "reply.interrupted": kind = AuditEventKind.ReplyInterrupted; return true;
            case "tool.failed": kind = AuditEventKind.ToolFailed; return true;
            case "call.ended": kind = AuditEventKind.CallEnded; return true;
            case "prompt.flagged": kind = AuditEventKind.PromptFlagged; return true;
            case "turn.superseded": kind = AuditEventKind.TurnSuperseded; return true;
            default: kind = default; return false;
        }
    }
}

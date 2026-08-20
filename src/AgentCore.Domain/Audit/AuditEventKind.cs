namespace AgentCore.Domain.Audit;

/// <summary>
/// The closed set of things one call writes into the audit chain.
/// </summary>
/// <remarks>
/// <para>
/// The set is closed on purpose. Section 11 names what the chain must hold, and a vocabulary that
/// grows by one string on every new caller cannot be read a year later. A new kind is a change to
/// this enum, a change to <see cref="AuditEventKinds"/>, and a new row in the public API file.
/// </para>
/// <para>
/// The chain is append-only, so nothing here corrects an earlier event in place. A correction is a
/// second event that names the first through <see cref="AuditEvent.AmendsSequence"/>. Triage row T23
/// settles this for barge-in: <see cref="ReplyInterrupted"/> amends the
/// <see cref="TurnCompleted"/> event of the same turn.
/// </para>
/// </remarks>
public enum AuditEventKind
{
    /// <summary>The call started. It is the first event of every call.</summary>
    CallStarted = 0,

    /// <summary>One turn ran to the end, and the caller heard the whole reply.</summary>
    TurnCompleted = 1,

    /// <summary>The caller spoke over a reply, so the reply stopped early.</summary>
    ReplyInterrupted = 2,

    /// <summary>A tool call failed, and the turn continued without its answer.</summary>

    ToolFailed = 3,

    /// <summary>The call ended. It is the last event of every call.</summary>
    CallEnded = 5,

    /// <summary>Moderation flagged what the CALLER said, so the agent refused to answer that turn.</summary>
    PromptFlagged = 6,
}

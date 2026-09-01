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
/// second event that names the first through <see cref="AuditEvent.AmendsEventId"/>. Triage row T23
/// settles this for barge-in: <see cref="ReplyInterrupted"/> amends the
/// <see cref="TurnCompleted"/> event of the same turn.
/// </para>
/// </remarks>
public enum AuditEventKind
{
    /// <summary>A session of the call started. It is the first event of every session, not of every call.</summary>
    /// <remarks>
    /// One per SESSION, and a call can have several: a chat page reload opens a second session onto
    /// the same call, so a resumed call's chain holds this event more than once, and the second one
    /// sits after the turns of the first. That is the honest record. Suppressing it on a resume would
    /// destroy the one fact an auditor most wants — that the call was picked up again — permanently,
    /// in the only store that keeps the long-term record.
    /// </remarks>
    CallStarted = 0,

    /// <summary>One turn ran to the end, and the caller heard the whole reply.</summary>
    TurnCompleted = 1,

    /// <summary>The caller spoke over a reply, so the reply stopped early.</summary>
    ReplyInterrupted = 2,

    /// <summary>A tool call failed, and the turn continued without its answer.</summary>

    ToolFailed = 3,

    /// <summary>The host ended the call. It is the last event of the session that ended it, not of the chain.</summary>
    /// <remarks>
    /// A session that ends a call does not stop another session opening onto the same call
    /// afterwards, so this can sit in the middle of a chain with a second
    /// <see cref="CallStarted"/> and more turns behind it. The wire token stays <c>call.ended</c>
    /// because rows already written mean what they meant: this session let the call go.
    /// </remarks>
    CallEnded = 5,

    /// <summary>Moderation flagged what the CALLER said, so the agent refused to answer that turn.</summary>
    PromptFlagged = 6,
}

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
    /// <remarks>One call writes exactly one of these, and it carries no turn index.</remarks>
    CallStarted = 0,

    /// <summary>One turn ran to the end, and the caller heard the whole reply.</summary>
    /// <remarks>
    /// It carries the turn index, the stage the turn ran in, the stage the machine holds after it,
    /// and the reply text the model produced.
    /// </remarks>
    TurnCompleted = 1,

    /// <summary>The caller spoke over a reply, so the reply stopped early.</summary>
    /// <remarks>
    /// <para>
    /// This event amends the <see cref="TurnCompleted"/> event of the same turn, per T23, because
    /// the chain refuses to rewrite the first event. <see cref="AuditEvent.AmendsSequence"/> is
    /// therefore required on this kind, and <see cref="AuditChain.Link"/> refuses the event without
    /// it.
    /// </para>
    /// <para>
    /// Section 11, item 6a: the event records the text the caller ACTUALLY HEARD, not the text the
    /// model produced. The value arrives in the relay's <c>utteranceUntilInterrupt</c> field, so it
    /// is reported and never estimated (T54). It is required on this kind, under
    /// <see cref="AuditPayloadKeys.UtteranceUntilInterrupt"/>.
    /// </para>
    /// </remarks>
    ReplyInterrupted = 2,

    /// <summary>A tool call failed, and the turn continued without its answer.</summary>
    /// <remarks>A failing tool never ends a call, so this event sits beside the turn and not instead of it.</remarks>
    ToolFailed = 3,

    /// <summary>Moderation flagged a reply.</summary>
    /// <remarks>Section 11, item 11: every turn passes the moderation endpoint, and a flagged reply is recorded here.</remarks>
    ReplyFlagged = 4,

    /// <summary>The call ended. It is the last event of every call.</summary>
    /// <remarks>One call writes exactly one of these, and it carries no turn index.</remarks>
    CallEnded = 5,
}

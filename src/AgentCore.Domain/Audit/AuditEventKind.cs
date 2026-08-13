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
    /// <remarks>
    /// <para>
    /// Section 11, item 11: every turn passes the moderation endpoint, and a flagged reply is
    /// recorded here.
    /// </para>
    /// <para>
    /// No code produces this kind today, because the owner moved the check to the caller's side, and
    /// <see cref="PromptFlagged"/> records that fact. The kind stays in the vocabulary anyway: a kind
    /// is stable forever, so a stored row keeps its meaning.
    /// </para>
    /// </remarks>
    ReplyFlagged = 4,

    /// <summary>The call ended. It is the last event of every call.</summary>
    /// <remarks>One call writes exactly one of these, and it carries no turn index.</remarks>
    CallEnded = 5,

    /// <summary>Moderation flagged what the CALLER said, so the agent refused to answer that turn.</summary>
    /// <remarks>
    /// <para>
    /// The owner moved the check to the caller's spoken input, ahead of the model, and this differs
    /// from section 11, item 11, which asked for the reply. The owner made that call knowingly.
    /// <see cref="ReplyFlagged"/> keeps its meaning and keeps its number.
    /// </para>
    /// <para>
    /// <b>It amends nothing.</b> The moderation verdict is known BEFORE the model runs, so
    /// <c>prompt.flagged</c> is written BEFORE the <see cref="TurnCompleted"/> event of the same
    /// turn. There is no earlier event to correct, so this kind does not carry
    /// <see cref="AuditEvent.AmendsSequence"/> the way <see cref="ReplyInterrupted"/> must.
    /// <see cref="AuditEvent.TurnIndex"/> names the turn the fact belongs to. The chain requires no
    /// amendment here, and it forbids none either.
    /// </para>
    /// <para>
    /// It carries <see cref="AuditPayloadKeys.ModerationCategories"/>, and
    /// <see cref="AuditChain.Link"/> refuses the event without it. The kind alone says something
    /// flagged the caller, and the categories are the only other fact the event holds. Section 9
    /// makes this chain the only long-term record, so the fact goes in with the event or it is lost.
    /// This is the argument the chain already makes for <c>utteranceUntilInterrupt</c> on
    /// <see cref="ReplyInterrupted"/>.
    /// </para>
    /// <para>
    /// <b>The category names are open, and the chain never checks them.</b> The taxonomy belongs to
    /// the moderation endpoint: <c>omni-moderation-latest</c> is a moving pointer, and OpenAI adds
    /// categories to it. A closed set here would make <see cref="AuditChain.Link"/> throw the first
    /// time a new category arrives, and destroy the exact record the chain exists to protect. This is
    /// the opposite of the <see cref="CallEndReason"/> decision, and the two differ for one reason:
    /// our own code produces an end reason from an enum it owns, and a category arrives off a vendor
    /// wire. The chain checks the SHAPE of the list and never the names in it.
    /// </para>
    /// </remarks>
    PromptFlagged = 6,
}

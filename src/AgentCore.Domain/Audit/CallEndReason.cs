namespace AgentCore.Domain.Audit;

/// <summary>
/// The closed set of ways one call ends.
/// </summary>
/// <remarks>
/// <para>
/// The set is closed for the reason the chain exists. D23 makes the audit table the record of the
/// call, and D26 buys 14 days of free retention on every other signal, so §9 calls this chain the
/// only long-term record. A report written years later reads this table and nothing else. A
/// free-text reason cannot be counted: one path writes
/// <c>hangup</c>, a second writes <c>caller hung up</c>, and a third writes <c>HANGUP</c>, and the
/// three are one fact under three names.
/// </para>
/// <para>
/// Section 4 names every way a call ends, and there are four. The caller hangs up, and only the
/// vendor adapter sees it. The stage machine reaches a terminal stage, and the agent finished the
/// work. The agent hands the call to a human through the conference pattern of section 4.3, which is
/// item 5 of section 11. Or a fault ends the call.
/// </para>
/// <para>
/// A caller that holds more detail puts it in another payload key. The vendor hang-up cause, the
/// terminal stage under <see cref="AuditPayloadKeys.StageAfter"/>, and the message of a fault are all
/// facts beside the reason, and none of them is the reason. The reason stays countable.
/// </para>
/// <para>
/// The set is small on purpose, and it stays small. A new reason is a change to this enum, a change
/// to <see cref="CallEndReasons"/>, and a new row in the public API file.
/// </para>
/// </remarks>
public enum CallEndReason
{
    /// <summary>The caller hung up.</summary>
    /// <remarks>
    /// Only the telephony adapter sees this, because the hang-up arrives on a Call Control webhook
    /// and never on the relay socket. The vendor reports a cause with it, and that cause is detail
    /// beside the reason rather than the reason.
    /// </remarks>
    CallerHungUp = 0,

    /// <summary>The stage machine reached a terminal stage, and the agent finished the call.</summary>
    /// <remarks>
    /// The turn loop closes the chain itself here, because it is the only place that knows the stage
    /// the machine reached. The event carries that stage under
    /// <see cref="AuditPayloadKeys.StageAfter"/>, so a reader still learns which ending it was.
    /// </remarks>
    AgentCompleted = 1,

    /// <summary>The call went to a human through the conference pattern.</summary>
    /// <remarks>
    /// Section 11, item 5, and section 4.3: the adapter joins the call to a conference and never
    /// sends the Telnyx <c>transfer</c> command, which costs $0.10 for each invocation (T27). The
    /// call leaves the agent at this point, so this is the last event of its chain.
    /// </remarks>
    TransferredToHuman = 2,

    /// <summary>A fault ended the call.</summary>
    /// <remarks>
    /// This is the ending section 8.7 works to prevent, so it is rare. A tool that fails and a run
    /// that answers with no text both speak the fallback line and keep the call, and neither reaches
    /// here. A dropped relay socket and a stage that names no agent do.
    /// </remarks>
    Faulted = 3,
}

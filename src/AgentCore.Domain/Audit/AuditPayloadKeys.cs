namespace AgentCore.Domain.Audit;

/// <summary>
/// The payload keys the audit vocabulary knows by name.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AuditEvent.Payload"/> takes any key, because a brand adds facts the library cannot
/// predict. These keys are the ones the design names, so a reader finds the same fact under the same
/// key in every call, and a query written today still runs next year.
/// </para>
/// <para>
/// A key is stable forever, for the same reason a kind token is. See <see cref="AuditEventKinds"/>.
/// </para>
/// </remarks>
public static class AuditPayloadKeys
{
    /// <summary>The text the caller ACTUALLY HEARD before the barge-in cut the reply.</summary>
    /// <remarks>
    /// Section 11, item 6a. The relay reports it in <c>utteranceUntilInterrupt</c>, so the chain
    /// records it rather than estimating it (T54). It is required on
    /// <see cref="AuditEventKind.ReplyInterrupted"/>.
    /// </remarks>
    public const string UtteranceUntilInterrupt = "utteranceUntilInterrupt";

    /// <summary>How long the caller heard, in milliseconds, before the barge-in cut the reply.</summary>
    /// <remarks>The relay reports it in <c>durationUntilInterruptMs</c>, at 1 ms resolution.</remarks>
    public const string DurationUntilInterruptMs = "durationUntilInterruptMs";

    /// <summary>The whole reply text the model produced.</summary>
    /// <remarks>
    /// A barge-in makes this differ from <see cref="UtteranceUntilInterrupt"/>, and reading the two
    /// together is what item 6a asks a reviewer to do.
    /// </remarks>
    public const string ReplyText = "replyText";

    /// <summary>The stage the turn ran in.</summary>
    public const string StageBefore = "stageBefore";

    /// <summary>The stage the machine holds after the turn.</summary>
    public const string StageAfter = "stageAfter";

    /// <summary>The name of the tool that failed.</summary>
    /// <remarks>
    /// It is the name the MODEL called, which for <see cref="Audit.ToolFailureKind.Undeclared"/> is a
    /// name the document does not declare. A reader that joins this to <c>tools[].id</c> therefore
    /// finds nothing for exactly the rows that matter most.
    /// </remarks>
    public const string ToolName = "toolName";

    /// <summary>
    /// The id the model gave the one tool call that failed, so two calls to the same tool in one turn
    /// are two records and not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A model may emit several <c>FunctionCallContent</c> in one assistant message, and nothing stops
    /// three of them from naming the same tool. <see cref="ToolName"/> alone then reads as one fact
    /// repeated, and the chain loses which call failed and which answered. The call id is the only
    /// thing that tells them apart, and it is also what joins this row to the tool result in the
    /// transcript and to the <c>gen_ai.tool.call.id</c> attribute on the framework's own
    /// <c>execute_tool</c> span.
    /// </para>
    /// <para>
    /// <b>Why this is not called <c>gen_ai.tool.call.id</c>.</b> The OpenTelemetry GenAI conventions
    /// name it that, and adopting a published vocabulary is normally the right instinct. It is refused
    /// here for three reasons, and the third is the one that decides it. First, every
    /// <c>gen_ai.*</c> attribute is still marked Development stability, and the conventions were moved
    /// out of the semantic-conventions repository in v1.42.0 into a repository with no tagged release
    /// at all, with the <c>execute_tool</c> attribute set restructured after that. Second, the payload
    /// beside it is already this vocabulary's own — <see cref="ToolName"/>, <see cref="ToolError"/>,
    /// <see cref="EndReason"/>, <see cref="ReplyText"/> — and one record written in two vocabularies
    /// reads as two records to whoever queries it. Third, and decisively: D15 makes this constant a
    /// permanent obligation and the row it keys is hash-chained and read years later, so the name must
    /// be one WE can promise. Borrowing an unstable name would be promising someone else's.
    /// </para>
    /// <para>
    /// <b>Why there is no schema version beside it.</b> A version key would have to ride every event to
    /// be worth reading, which changes the canonical bytes of every row for a rename that may never
    /// come. It is also not needed: the canonical form is ALREADY versioned, in the first line of every
    /// hashed record, by <see cref="AuditChain.CanonicalFormVersion"/>, so a change to the hashing rule
    /// leaves every stored row verifiable under the rule it was written with. And a key is never
    /// renamed in place — a new key is added beside the old one, exactly as a wire token is — because
    /// "a missing fact is an absent key" already makes an absent old key readable to a new reader. The
    /// migration path therefore exists without spending bytes on every row.
    /// </para>
    /// </remarks>
    public const string ToolCallId = "toolCallId";

    /// <summary>Which of the two ways a tool call fails this one was.</summary>
    /// <remarks>
    /// The value is one wire token of <see cref="Audit.ToolFailureKind"/>, and never free text, for the
    /// reason <see cref="EndReason"/> gives: the fact is counted years later. It is not required on
    /// <see cref="AuditEventKind.ToolFailed"/> — a turn whose run threw before any one call could be
    /// named still writes the event, and a missing fact is an absent key.
    /// </remarks>
    public const string ToolFailureKind = "toolFailureKind";

    /// <summary>Why the tool failed, in the words the tool gave.</summary>
    /// <remarks>
    /// It stays free text. The words come from an arbitrary tool body or an arbitrary endpoint, so
    /// there is no set here to close; the countable half of the fact is
    /// <see cref="ToolFailureKind"/> beside it.
    /// </remarks>
    public const string ToolError = "toolError";

    /// <summary>
    /// The moderation categories that flagged the caller's spoken input, comma-separated and in the
    /// order the endpoint returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is required on <see cref="AuditEventKind.PromptFlagged"/>, and
    /// <see cref="AuditChain.Link"/> refuses the event without it. No member of the list is empty, so
    /// a reader splits on a comma and counts. The order is the endpoint's, and the canonical form
    /// keeps it, because the chain sorts payload keys and never a payload value.
    /// </para>
    /// <para>
    /// The taxonomy is the endpoint's and it is open. The chain checks the shape of the list and
    /// never the names in it, because <c>omni-moderation-latest</c> is a moving pointer and OpenAI
    /// adds categories to it. See <see cref="AuditEventKind.PromptFlagged"/> for why this differs
    /// from <see cref="EndReason"/>.
    /// </para>
    /// </remarks>
    public const string ModerationCategories = "moderationCategories";

    /// <summary>Why the call ended.</summary>
    /// <remarks>
    /// The value is one wire token of <see cref="CallEndReason"/>, and never free text. The reason is
    /// counted years later, so it is closed. Detail beside it goes under another key, such as the
    /// terminal stage under <see cref="StageAfter"/>. It is required on
    /// <see cref="AuditEventKind.CallEnded"/>.
    /// </remarks>
    public const string EndReason = "endReason";
}

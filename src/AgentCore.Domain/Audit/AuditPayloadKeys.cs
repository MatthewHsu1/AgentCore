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
    public const string ToolName = "toolName";

    /// <summary>Why the tool failed, in the words the tool gave.</summary>
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

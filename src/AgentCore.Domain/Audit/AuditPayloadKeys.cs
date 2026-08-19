namespace AgentCore.Domain.Audit;

/// <summary>
/// The payload keys the audit vocabulary knows by name.
/// </summary>
public static class AuditPayloadKeys
{
    /// <summary>
    /// The SHA-256 of the text the caller ACTUALLY HEARD before the barge-in cut the reply, as 64
    /// lowercase hexadecimal characters.
    /// </summary>
    public const string UtteranceUntilInterruptSha256 = "utteranceUntilInterruptSha256";

    /// <summary>
    /// How long the caller heard, in milliseconds, before the barge-in cut the reply.
    /// </summary>
    public const string DurationUntilInterruptMs = "durationUntilInterruptMs";

    /// <summary>
    /// The SHA-256 of the whole reply text the model produced, as 64 lowercase hexadecimal
    /// characters.
    /// </summary>
    public const string ReplyTextSha256 = "replyTextSha256";

    /// <summary>
    /// The stage the turn ran in.
    /// </summary>
    public const string StageBefore = "stageBefore";

    /// <summary>
    /// The stage the machine holds after the turn.
    /// </summary>
    public const string StageAfter = "stageAfter";

    /// <summary>
    /// The name of the tool that failed.
    /// </summary>
    public const string ToolName = "toolName";

    /// <summary>
    /// The id the model gave the one tool call that failed, so two calls to the same tool in one turn
    /// are two records and not one.
    /// </summary>
    public const string ToolCallId = "toolCallId";

    /// <summary>
    /// Which of the two ways a tool call fails this one was.
    /// </summary>
    /// <remarks>
    public const string ToolFailureKind = "toolFailureKind";

    /// <summary>
    /// Why the tool failed, in the words the tool gave.
    /// </summary>
    public const string ToolError = "toolError";

    /// <summary>
    /// The moderation categories that flagged the caller's spoken input, comma-separated and in the
    /// order the endpoint returned.
    /// </summary>
    public const string ModerationCategories = "moderationCategories";

    /// <summary>
    /// Why the call ended.
    /// </summary>
    public const string EndReason = "endReason";
}

namespace AgentCore.Application.Runtime;

/// <summary>Where a turn's words sit in the conversation the caller can see.</summary>
/// <param name="MessageId">
/// What the caller calls the message it is sending, or <see langword="null"/> to let the call name
/// it. The name is the caller's, and AgentCore reads it only as the handle a later edit anchors on.
/// </param>
/// <param name="ParentMessageId">
/// The message these words hang off, or <see langword="null"/> when they start the call afresh.
/// Read only when <see cref="NamesParent"/> is set.
/// </param>
public sealed record CallTurnOrigin(string? MessageId, string? ParentMessageId)
{
    /// <summary>Gets whether the caller answered for where this turn hangs.</summary>
    public required bool NamesParent { get; init; }
}

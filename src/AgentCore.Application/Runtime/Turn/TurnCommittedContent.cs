using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// The ids a turn's commit wrote, on the last update of the turn. A streaming caller never sees the ids any
    /// other way, because the framework's hook runs after the last update.
    /// </summary>
    /// <param name="userMessageId">What the user's message was written under.</param>
    /// <param name="replyMessageId">What the last message was written under, or <see langword="null"/> when the user's is the only one.</param>
    internal sealed class TurnCommittedContent(string userMessageId, string? replyMessageId) : AIContent
    {
        /// <summary>Gets what the user's message was written under.</summary>
        public string UserMessageId { get; } = userMessageId;

        /// <summary>Gets what the last message was written under, or <see langword="null"/> when the user's is the only one.</summary>
        public string? ReplyMessageId { get; } = replyMessageId;
    }
}

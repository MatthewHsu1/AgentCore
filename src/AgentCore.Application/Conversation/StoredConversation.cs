using AgentCore.Application.Blobs;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;

namespace AgentCore.Application.Conversation
{
    /// <summary>One conversation as the stores hold it: ready for a browser, nothing left to join.</summary>
    /// <param name="Conversation">The row.</param>
    /// <param name="Messages">The stored messages, oldest first: every one, or one window's worth.</param>
    /// <param name="Files">
    /// The facts of each file the messages name and the blob store kept, each name once, in first-seen order.
    /// A link to the bytes comes from <see cref="IConversations.LinkFileAsync"/> when the person asks for one.
    /// </param>
    public sealed record StoredConversation(
        ConversationRecord Conversation,
        IReadOnlyList<ConversationMessage> Messages,
        IReadOnlyList<BlobRef> Files)
    {
        /// <summary>
        /// Gets the turn to read the next older window before, or <see langword="null"/> when
        /// <see cref="Messages"/> reaches the conversation's start — or holds the whole conversation.
        /// </summary>
        public int? OlderBefore { get; init; }
    }
}

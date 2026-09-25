using AgentCore.Application.Runtime;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// Owns the session of a conversation for as long as the conversation needs it.
    /// </summary>
    public interface IConversationSessions
    {
        /// <summary>Opens the session of one conversation.</summary>
        /// <param name="conversationId">The id the host gives the conversation, or <see langword="null"/> to be given one.</param>
        /// <param name="cancellationToken">Cancels the open.</param>
        /// <returns>The session, ready for its first turn.</returns>
        ValueTask<ConversationSession> OpenAsync(string? conversationId, CancellationToken cancellationToken = default);

        /// <summary>Reads the session of one conversation, and marks the conversation as still live.</summary>
        /// <param name="conversationId">The id the request named. It is the <see cref="ConversationSession.ConversationId"/>.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The session, or <see langword="null"/> when this holds no such conversation.</returns>
        ValueTask<ConversationSession?> TryGetAsync(string conversationId, CancellationToken cancellationToken = default);

        /// <summary>Ends one conversation: waits for the words it still owes store 1, then drops it.</summary>
        /// <param name="conversationId">The id of the conversation that ended.</param>
        /// <param name="cancellationToken">Cancels the close.</param>
        /// <returns>A task that completes once the session is gone and its writes are done.</returns>
        ValueTask CloseAsync(string conversationId, CancellationToken cancellationToken = default);
    }
}

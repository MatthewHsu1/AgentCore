using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports
{
    /// <summary>Makes a conversation's title from its words.</summary>
    public interface IConversationTitler
    {
        /// <summary>Generates one conversation's title, streaming it as it arrives.</summary>
        /// <param name="conversationId">The conversation to title.</param>
        /// <param name="cancellationToken">Stops the generation.</param>
        /// <returns>The title in pieces, in order.</returns>
        IAsyncEnumerable<string> GenerateAsync(string conversationId, CancellationToken cancellationToken = default);

        // A turn reaches store 1 only once it has finished, so a browser naming a conversation the
        // moment it starts finds nothing there to read. It hands over what it is showing instead.
        /// <summary>Generates one conversation's title from messages the caller holds, streaming it as it arrives.</summary>
        /// <param name="conversationId">The conversation to title. Its stored messages are not read.</param>
        /// <param name="messages">The messages to name, with their roles. Empty leaves the conversation unnamed.</param>
        /// <param name="cancellationToken">Stops the generation.</param>
        /// <returns>The title in pieces, in order.</returns>
        IAsyncEnumerable<string> GenerateFromAsync(
            string conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
    }
}

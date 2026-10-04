namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>
    /// What the caller did, for one conversation, as one ordered stream.
    /// </summary>
    public interface IConversationInputPort : IAsyncDisposable
    {
        /// <summary>Reads what the caller does, until the conversation ends.</summary>
        /// <param name="cancellationToken">
        /// Abandons the read. Cancelling it throws, and never ends the stream quietly.
        /// </param>
        /// <returns>Every inbound event of the conversation, in order.</returns>
        /// <exception cref="InvalidOperationException">This port is already being read.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
        IAsyncEnumerable<ConversationInput> ListenAsync(CancellationToken cancellationToken = default);
    }
}

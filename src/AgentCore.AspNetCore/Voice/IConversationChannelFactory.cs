namespace AgentCore.AspNetCore.Voice
{
    /// <summary>Opens the channel for one conversation.</summary>
    public interface IConversationChannelFactory
    {
        /// <summary>Opens both halves of one conversation.</summary>
        /// <param name="context">What the conversation is.</param>
        /// <param name="cancellationToken">Abandons the attempt.</param>
        /// <returns>The pair. The caller disposes it when the conversation ends.</returns>
        ValueTask<ConversationChannel> OpenAsync(ConversationChannelContext context, CancellationToken cancellationToken = default);
    }
}

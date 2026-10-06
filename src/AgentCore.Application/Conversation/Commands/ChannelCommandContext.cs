namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>The conversation an <see cref="IChannelCommandHandler{TCommand, TOutcome}"/> acts on.</summary>
    public sealed record ChannelCommandContext
    {
        /// <summary>Gets the conversation the channel carries.</summary>
        public required string ConversationId { get; init; }

        /// <summary>
        /// Gets the call's own transport headers, such as SIP headers, by name, case-insensitive. A host finds its phone
        /// system's id for the call here when the system that placed the call put it in.
        /// </summary>
        public required IReadOnlyDictionary<string, string> Headers { get; init; }
    }
}

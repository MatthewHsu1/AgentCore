namespace AgentCore.Application.Conversation.Actions
{
    /// <summary>One transfer an <see cref="ICallTransfer"/> carries out.</summary>
    public sealed record CallTransfer
    {
        /// <summary>Gets the conversation the call carries.</summary>
        public required string ConversationId { get; init; }

        /// <summary>Gets the line, as the <see cref="TransferAction"/> named it.</summary>
        public required Uri Target { get; init; }

        /// <summary>
        /// Gets the call's own transport headers, such as SIP headers, by name, case-insensitive. A host finds its phone
        /// system's id for the call here when the system that placed the call put it in.
        /// </summary>
        public required IReadOnlyDictionary<string, string> Headers { get; init; }
    }
}

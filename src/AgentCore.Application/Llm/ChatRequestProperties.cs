namespace AgentCore.Application.Llm
{
    /// <summary>
    /// The keys AgentCore writes on <c>ChatOptions.AdditionalProperties</c> for a vendor adapter to
    /// read. The runtime knows the conversation; the adapter knows the vendor's wire field. These
    /// keys are the seam between the two.
    /// </summary>
    public static class ChatRequestProperties
    {
        /// <summary>
        /// The id of the conversation this request belongs to, as a <see cref="string"/>. An adapter
        /// hands it to the vendor as the prompt cache key, so every turn of one conversation routes to
        /// the same cache. Absent on a request that runs outside a conversation turn.
        /// </summary>
        public const string ConversationId = "urn:agentcore:conversation-id";
    }
}

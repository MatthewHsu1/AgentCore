namespace AgentCore.Application.Conversation
{
    /// <summary>Whether a conversation still belongs in a caller's list.</summary>
    public enum ConversationStatus
    {
        /// <summary>Listed as usual.</summary>
        Regular,

        /// <summary>Kept, but out of the way.</summary>
        Archived,
    }
}

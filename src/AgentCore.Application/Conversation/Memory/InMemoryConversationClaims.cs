namespace AgentCore.Application.Conversation.Memory
{
    /// <summary>
    /// Which principal may see which conversation, held beside the in-memory store's conversations. Every
    /// member runs under the owning store's lock; nothing here takes one.
    /// </summary>
    internal sealed class InMemoryConversationClaims
    {
        private readonly HashSet<(string ConversationId, string PrincipalKey)> _claims = [];

        /// <summary>Gives a principal a claim on a conversation.</summary>
        /// <param name="conversationId">The conversation to claim.</param>
        /// <param name="principalKey">The opaque key that claims it.</param>
        public void Attach(string conversationId, string principalKey)
        {
            _ = _claims.Add((conversationId, principalKey));
        }

        /// <summary>Takes a principal's claim off a conversation.</summary>
        /// <param name="conversationId">The conversation to unclaim.</param>
        /// <param name="principalKey">The key to remove.</param>
        public void Detach(string conversationId, string principalKey)
        {
            _ = _claims.Remove((conversationId, principalKey));
        }

        /// <summary>Reads the ids of the conversations one principal has claimed.</summary>
        /// <param name="principalKey">The opaque key to look up.</param>
        public IEnumerable<string> ConversationsOf(string principalKey)
        {
            return _claims.Where(claim => claim.PrincipalKey == principalKey).Select(claim => claim.ConversationId);
        }

        /// <summary>Withdraws every claim on a conversation.</summary>
        /// <param name="conversationId">The conversation that is going.</param>
        public void Forget(string conversationId)
        {
            _ = _claims.RemoveWhere(claim => claim.ConversationId == conversationId);
        }
    }
}

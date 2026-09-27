namespace AgentCore.Application.Conversation.Memory
{
    /// <summary>
    /// One row per response id, held beside the in-memory store's conversations.
    /// </summary>
    internal sealed class InMemoryConversationContinuations
    {
        private readonly Dictionary<string, (string ConversationId, DateTimeOffset CreatedAt)> _rows = [];

        /// <summary>
        /// Records that one response id continues one conversation.
        /// </summary>
        /// <param name="responseId">The response id to record.</param>
        /// <param name="conversationId">The conversation the response id continues.</param>
        /// <param name="createdAt">When this row was written, for the retention sweep.</param>
        public void Save(string responseId, string conversationId, DateTimeOffset createdAt)
        {
            if (!_rows.ContainsKey(responseId))
            {
                _rows[responseId] = (conversationId, createdAt);
            }
        }

        /// <summary>Finds the conversation one response id continues.</summary>
        /// <param name="responseId">The response id to look up.</param>
        /// <returns>The conversation id, or <see langword="null"/> when no row names that response id.</returns>
        public string? Find(string responseId)
        {
            return _rows.TryGetValue(responseId, out (string ConversationId, DateTimeOffset CreatedAt) row)
                ? row.ConversationId
                : null;
        }

        /// <summary>Withdraws one response id, if it names a row.</summary>
        /// <param name="responseId">The response id to forget.</param>
        public void Forget(string responseId)
        {
            _ = _rows.Remove(responseId);
        }

        /// <summary>Withdraws every response id filed under one conversation.</summary>
        /// <param name="conversationId">The conversation whose response ids are gone.</param>
        public void ForgetConversation(string conversationId)
        {
            foreach (string responseId in _rows
                .Where(pair => pair.Value.ConversationId == conversationId)
                .Select(pair => pair.Key)
                .ToList())
            {
                _ = _rows.Remove(responseId);
            }
        }

        /// <summary>
        /// Withdraws every response id written before the cutoff, whichever conversation it continues.
        /// </summary>
        /// <param name="cutoff">The point in time a row must have been written on or after, to stay.</param>
        /// <returns>How many rows went.</returns>
        public int Sweep(DateTimeOffset cutoff)
        {
            List<string> going = [.. _rows
                .Where(pair => pair.Value.CreatedAt < cutoff)
                .Select(pair => pair.Key)];

            foreach (string responseId in going)
            {
                _ = _rows.Remove(responseId);
            }

            return going.Count;
        }
    }
}

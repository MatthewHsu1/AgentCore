namespace AgentCore.Application.Conversation.Memory
{
    /// <summary>The busy mark of each conversation the in-memory store holds, beside its rows rather than on them.</summary>
    /// <param name="time">The clock a mark lapses on.</param>
    internal sealed class InMemoryConversationBusyMarks(TimeProvider time)
    {
        private readonly Lock _lock = new();

        private readonly Dictionary<string, (string Holder, DateTimeOffset Until)> _marks = [];

        /// <summary>Puts or extends a holder's mark, unless another holder's mark is still live.</summary>
        /// <param name="conversationId">The conversation to mark.</param>
        /// <param name="holder">Who holds the mark.</param>
        /// <param name="lease">How long the mark lives from now.</param>
        /// <returns><see langword="true"/> when <paramref name="holder"/> now holds the mark.</returns>
        public bool TryMark(string conversationId, string holder, TimeSpan lease)
        {
            lock (_lock)
            {
                DateTimeOffset now = time.GetUtcNow();

                if (_marks.TryGetValue(conversationId, out (string Holder, DateTimeOffset Until) mark)
                    && mark.Holder != holder
                    && mark.Until > now)
                {
                    return false;
                }

                _marks[conversationId] = (holder, now + lease);
                return true;
            }
        }

        /// <summary>Takes a holder's mark off, and leaves another holder's mark as it is.</summary>
        /// <param name="conversationId">The conversation to clear.</param>
        /// <param name="holder">The id the mark was put under.</param>
        public void Clear(string conversationId, string holder)
        {
            lock (_lock)
            {
                if (_marks.TryGetValue(conversationId, out (string Holder, DateTimeOffset Until) mark) && mark.Holder == holder)
                {
                    _ = _marks.Remove(conversationId);
                }
            }
        }
    }
}

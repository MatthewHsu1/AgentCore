using System.Text.Json;

namespace AgentCore.Application.Conversation.Memory
{
    /// <summary>
    /// One serialized agent session per continuation id, held beside the in-memory store's conversations.
    /// Every member runs under the owning store's lock; nothing here takes one.
    /// </summary>
    internal sealed class InMemoryConversationContinuations
    {
        private readonly Dictionary<string, (string ConversationId, JsonElement Envelope, DateTimeOffset UpdatedAt)> _envelopes = [];

        /// <summary>Files one session envelope under one continuation id, replacing any envelope already there.</summary>
        /// <param name="continuationId">The continuation id: a conversation id or a response id.</param>
        /// <param name="conversationId">The conversation the continuation belongs to.</param>
        /// <param name="envelope">The serialized session, as the agent wrote it.</param>
        /// <param name="updatedAt">When this write happened. A rewrite of the same id resets its retention clock.</param>
        public void Save(string continuationId, string conversationId, JsonElement envelope, DateTimeOffset updatedAt)
        {
            _envelopes[continuationId] = (conversationId, envelope.Clone(), updatedAt);
        }

        /// <summary>Reads the envelope one continuation id names.</summary>
        /// <param name="continuationId">The continuation id to look up.</param>
        /// <returns>The envelope, or <see langword="null"/> when nothing is filed under that id.</returns>
        public JsonElement? Get(string continuationId)
        {
            return _envelopes.TryGetValue(continuationId, out (string ConversationId, JsonElement Envelope, DateTimeOffset UpdatedAt) row)
                ? row.Envelope
                : null;
        }

        /// <summary>Withdraws whatever one continuation id names, if anything.</summary>
        /// <param name="continuationId">The continuation id to forget.</param>
        public void Forget(string continuationId)
        {
            _ = _envelopes.Remove(continuationId);
        }

        /// <summary>Withdraws every continuation filed under one conversation, however it was keyed.</summary>
        /// <param name="conversationId">The conversation whose continuations are gone.</param>
        public void ForgetConversation(string conversationId)
        {
            foreach (string continuationId in _envelopes
                .Where(pair => pair.Value.ConversationId == conversationId)
                .Select(pair => pair.Key)
                .ToList())
            {
                _ = _envelopes.Remove(continuationId);
            }
        }

        /// <summary>
        /// Withdraws every continuation row untouched since before the cutoff, whichever conversation
        /// it belongs to.
        /// </summary>
        /// <param name="cutoff">The point in time a row must have been written on or after, to stay.</param>
        /// <returns>How many rows went.</returns>
        public int Sweep(DateTimeOffset cutoff)
        {
            List<string> going = [.. _envelopes
                .Where(pair => pair.Value.UpdatedAt < cutoff)
                .Select(pair => pair.Key)];

            foreach (string continuationId in going)
            {
                _ = _envelopes.Remove(continuationId);
            }

            return going.Count;
        }
    }
}

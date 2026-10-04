namespace AgentCore.Application.Conversation.Memory
{
    /// <summary>
    /// The resume state of each conversation, kept beside its row rather than on it. A state never replaces a newer
    /// one. The store's lock guards every call.
    /// </summary>
    internal sealed class InMemoryConversationStates
    {
        private readonly Dictionary<string, ConversationSessionState> _states = new(StringComparer.Ordinal);

        /// <summary>Reads a conversation's state, or <see langword="null"/> when none is stored.</summary>
        internal ConversationSessionState? Of(string conversationId)
        {
            return _states.GetValueOrDefault(conversationId);
        }

        /// <summary>Reads the turn a conversation takes next, by its stored state.</summary>
        internal int NextTurnIndex(string conversationId)
        {
            return Of(conversationId)?.NextTurnIndex ?? 0;
        }

        /// <summary>Keeps a state, unless it is <see langword="null"/> or the stored one names a later next turn.</summary>
        internal void Save(string conversationId, ConversationSessionState? state)
        {
            if (state is not null && state.NextTurnIndex >= NextTurnIndex(conversationId))
            {
                _states[conversationId] = state;
            }
        }

        /// <summary>Drops a conversation's state.</summary>
        internal void Forget(string conversationId)
        {
            _ = _states.Remove(conversationId);
        }
    }
}

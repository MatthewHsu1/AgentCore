using System.Text.Json;

namespace AgentCore.Application.Conversation.Memory;

/// <summary>
/// One serialized agent session per continuation id, held beside the in-memory store's conversations.
/// Every member runs under the owning store's lock; nothing here takes one.
/// </summary>
internal sealed class InMemoryConversationContinuations
{
    private readonly Dictionary<string, JsonElement> _envelopes = [];

    /// <summary>Files one session envelope under one continuation id, replacing any envelope already there.</summary>
    /// <param name="continuationId">The continuation id: a conversation id or a response id.</param>
    /// <param name="envelope">The serialized session, as the agent wrote it.</param>
    public void Save(string continuationId, JsonElement envelope) => _envelopes[continuationId] = envelope.Clone();

    /// <summary>Reads the envelope one continuation id names.</summary>
    /// <param name="continuationId">The continuation id to look up.</param>
    /// <returns>The envelope, or <see langword="null"/> when nothing is filed under that id.</returns>
    public JsonElement? Get(string continuationId)
        => _envelopes.TryGetValue(continuationId, out var envelope) ? envelope : null;

    /// <summary>Withdraws whatever one continuation id names, if anything.</summary>
    /// <param name="continuationId">The continuation id to forget.</param>
    public void Forget(string continuationId) => _envelopes.Remove(continuationId);
}

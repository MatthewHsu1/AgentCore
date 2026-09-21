using System.Text.Json;
using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

namespace AgentCore.AspNetCore.Sessions
{
    /// <summary>
    /// The framework's session seam over the continuation map that lives in the conversation store.
    /// </summary>
    public sealed class AgentCoreAgentSessionStore : AgentSessionStore
    {
        private const string PointerProperty = "$continuationPointer";

        private readonly IConversationStore _conversations;

        /// <summary>Creates the seam over one conversation store.</summary>
        /// <param name="conversations">The store the continuation ids open onto.</param>
        /// <exception cref="ArgumentNullException">The store is <see langword="null"/>.</exception>
        public AgentCoreAgentSessionStore(IConversationStore conversations)
        {
            ArgumentNullException.ThrowIfNull(conversations);
            _conversations = conversations;
        }

        /// <summary>Reads whether anything is filed under one continuation id.</summary>
        /// <param name="sessionStoreId">The conversation id or response id to look up.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns><see langword="true"/> when a later load would find an envelope.</returns>
        public async ValueTask<bool> ContainsAsync(string sessionStoreId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sessionStoreId);

            return await _conversations.GetContinuationAsync(sessionStoreId, cancellationToken).ConfigureAwait(false)
                is not null;
        }

        /// <inheritdoc />
        public override ValueTask SaveSessionAsync(
            AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
        {
            return SaveSessionForConversationAsync(agent, sessionStoreId, sessionStoreId, session, cancellationToken);
        }

        /// <summary>Files the session under one continuation id, on behalf of one conversation.</summary>
        /// <param name="agent">The agent the session belongs to.</param>
        /// <param name="sessionStoreId">The conversation id or response id to file the envelope under.</param>
        /// <param name="conversationId">The conversation the continuation belongs to.</param>
        /// <param name="session">The session to file.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        public async ValueTask SaveSessionForConversationAsync(
            AIAgent agent, string sessionStoreId, string conversationId, AgentSession session, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(sessionStoreId);
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentNullException.ThrowIfNull(session);

            JsonElement envelope = await agent
                .SerializeSessionAsync(session, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await _conversations.SaveContinuationAsync(sessionStoreId, conversationId, envelope, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Points one continuation id at another, in place of a full envelope. A resume through the
        /// pointer id always reads the latest state, without a second copy of it.
        /// </summary>
        /// <param name="sessionStoreId">The continuation id the pointer is filed under.</param>
        /// <param name="conversationId">The conversation the pointer belongs to.</param>
        /// <param name="target">The continuation id holding the full envelope.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        public ValueTask SavePointerAsync(
            string sessionStoreId, string conversationId, string target, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sessionStoreId);
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentNullException.ThrowIfNull(target);

            JsonElement pointer = JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { [PointerProperty] = target });

            return _conversations.SaveContinuationAsync(sessionStoreId, conversationId, pointer, cancellationToken);
        }

        /// <inheritdoc />
        public override async ValueTask<AgentSession> GetSessionAsync(
            AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(sessionStoreId);

            JsonElement? envelope = await _conversations.GetContinuationAsync(sessionStoreId, cancellationToken).ConfigureAwait(false);

            if (envelope is null)
            {
                return await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            }

            if (TryReadPointerTarget(envelope.Value, out string? target))
            {
                envelope = await _conversations.GetContinuationAsync(target, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Continuation '{sessionStoreId}' points at '{target}', which no longer names an envelope.");
            }

            return await agent
                .DeserializeSessionAsync(envelope.Value, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Reads the target a pointer row names, or answers <see langword="false"/> for a full envelope.</summary>
        private static bool TryReadPointerTarget(JsonElement envelope, out string target)
        {
            if (envelope.ValueKind == JsonValueKind.Object
                && envelope.TryGetProperty(PointerProperty, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                target = value.GetString()!;
                return true;
            }

            target = "";
            return false;
        }

        /// <inheritdoc />
        public override ValueTask DeleteSessionAsync(
            AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(sessionStoreId);

            return _conversations.DeleteContinuationAsync(sessionStoreId, cancellationToken);
        }
    }
}

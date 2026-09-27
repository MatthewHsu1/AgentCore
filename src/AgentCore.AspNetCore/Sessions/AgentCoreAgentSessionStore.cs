using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

namespace AgentCore.AspNetCore.Sessions
{
    /// <summary>
    /// The framework's session seam over the conversation store.
    /// </summary>
    public sealed class AgentCoreAgentSessionStore : AgentSessionStore
    {
        private readonly IConversationStore _conversations;

        /// <summary>Creates the seam over one conversation store.</summary>
        /// <param name="conversations">The store the continuation ids open onto.</param>
        /// <exception cref="ArgumentNullException">The store is <see langword="null"/>.</exception>
        public AgentCoreAgentSessionStore(IConversationStore conversations)
        {
            ArgumentNullException.ThrowIfNull(conversations);
            _conversations = conversations;
        }

        /// <summary>Reads whether a continuation id names a conversation, directly or through a response id.</summary>
        /// <param name="sessionStoreId">The conversation id or response id to look up.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns><see langword="true"/> when a later resume would find the conversation to open.</returns>
        public async ValueTask<bool> ContainsAsync(string sessionStoreId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sessionStoreId);

            return await ResolveConversationIdAsync(sessionStoreId, cancellationToken).ConfigureAwait(false) is not null;
        }

        /// <summary>Resolves a continuation id to the conversation it names.</summary>
        /// <param name="sessionStoreId">A conversation id, or a response id that continues one.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>
        /// The conversation id: <paramref name="sessionStoreId"/> itself when it already names a conversation
        /// row, the row a response id continues, or <see langword="null"/> when it names neither.
        /// </returns>
        public async ValueTask<string?> ResolveConversationIdAsync(string sessionStoreId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sessionStoreId);

            if (await _conversations.FindContinuationAsync(sessionStoreId, cancellationToken).ConfigureAwait(false) is { } owner)
            {
                return owner;
            }

            return await _conversations.GetAsync(sessionStoreId, cancellationToken).ConfigureAwait(false) is not null
                ? sessionStoreId
                : null;
        }

        /// <summary>Records that one response id continues one conversation.</summary>
        /// <param name="responseId">The id minted for a turn's answer.</param>
        /// <param name="conversationId">The conversation the response id continues.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        public ValueTask SaveContinuationAsync(string responseId, string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(responseId);
            ArgumentNullException.ThrowIfNull(conversationId);

            return _conversations.SaveContinuationAsync(responseId, conversationId, cancellationToken);
        }

        /// <summary>
        /// Records that a response id continues the conversation MAF's <c>SaveSessionAsync</c> path just
        /// filed a session for. Nothing is written when the id already names the conversation itself: a
        /// conversation resumes through <see cref="GetSessionAsync"/> without a row of its own.
        /// </summary>
        /// <param name="agent">The agent the session belongs to.</param>
        /// <param name="sessionStoreId">The id the caller is filing under.</param>
        /// <param name="session">The session this store opened <paramref name="sessionStoreId"/> onto.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <exception cref="ArgumentException"><paramref name="session"/> is not one <see cref="AgentCoreAgent"/> created.</exception>
        public override ValueTask SaveSessionAsync(
            AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(sessionStoreId);
            ArgumentNullException.ThrowIfNull(session);

            if (session is not AgentCoreAgentSession own)
            {
                throw new ArgumentException(
                    $"Incompatible session type: {session.GetType()}. Only a session {nameof(AgentCoreAgent)} "
                    + "created can be filed.",
                    nameof(session));
            }

            string conversationId = own.Conversation.ConversationId;

            return string.Equals(sessionStoreId, conversationId, StringComparison.Ordinal)
                ? ValueTask.CompletedTask
                : _conversations.SaveContinuationAsync(sessionStoreId, conversationId, cancellationToken);
        }

        /// <inheritdoc />
        public override async ValueTask<AgentSession> GetSessionAsync(
            AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(sessionStoreId);

            // Unknown to store 0: opened under the id itself, not a random one, so this key resolves by
            // GetAsync forever after, and MAF's own SaveSessionAsync writes no response row for it at all
            // — a made-up id would need one, and the retention sweep would eventually take it.
            string conversationId = await ResolveConversationIdAsync(sessionStoreId, cancellationToken).ConfigureAwait(false)
                ?? sessionStoreId;

            return await GetSessionForConversationAsync(agent, conversationId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Opens the conversation a resolved id names.</summary>
        /// <param name="agent">The agent to open the conversation on.</param>
        /// <param name="conversationId">The conversation to open, or start under that id if it is new.</param>
        /// <param name="cancellationToken">Cancels the open.</param>
        /// <exception cref="ArgumentException"><paramref name="agent"/> is not one <see cref="AgentCoreAgent"/>.</exception>
        public ValueTask<AgentSession> GetSessionForConversationAsync(
            AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(conversationId);

            return agent is AgentCoreAgent own
                ? own.CreateSessionAsync(conversationId, cancellationToken)
                : throw new ArgumentException(
                    $"Incompatible agent type: {agent.GetType()}. Only {nameof(AgentCoreAgent)} can resume a "
                    + "conversation by id.",
                    nameof(agent));
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

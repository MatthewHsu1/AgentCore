using AgentCore.AspNetCore.Sessions;
using Microsoft.Agents.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>Files a session the Responses path just advanced, under every id that names it.</summary>
    internal static class ResponsesSessionFiling
    {
        /// <summary>
        /// Files the session the turn just advanced, under every id that names it now. The response id
        /// always gets the frozen, full envelope; the conversation id gets a pointer at it, so the two
        /// never hold two copies of the same state.
        /// </summary>
        /// <param name="sessions">The store to file the envelope in.</param>
        /// <param name="agent">The agent the session belongs to.</param>
        /// <param name="session">The session the turn advanced.</param>
        /// <param name="responseId">The id minted for this turn's answer.</param>
        /// <param name="conversationId">The client-facing conversation id, or <see langword="null"/> when none names it.</param>
        /// <param name="ownerConversationId">The internal conversation the continuation rows belong to, always present.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        internal static async Task SaveAsync(
            AgentCoreAgentSessionStore sessions,
            AIAgent agent,
            AgentSession session,
            string responseId,
            string? conversationId,
            string ownerConversationId,
            CancellationToken cancellationToken)
        {
            await sessions.SaveSessionForConversationAsync(agent, responseId, ownerConversationId, session, cancellationToken).ConfigureAwait(false);

            if (conversationId is not null)
            {
                await sessions.SavePointerAsync(conversationId, ownerConversationId, responseId, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

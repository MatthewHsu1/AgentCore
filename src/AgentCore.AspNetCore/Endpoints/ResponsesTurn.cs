using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>One turn the Responses path is about to run, and every id the answer files under.</summary>
    /// <param name="Agent">The entry the URL named.</param>
    /// <param name="Sessions">The store the session is filed in after the turn.</param>
    /// <param name="Session">The session the turn advances.</param>
    /// <param name="Conversation">The conversation inside <paramref name="Session"/>.</param>
    /// <param name="Input">The user words, or the approval answer, the turn runs on.</param>
    /// <param name="Origin">Where the request said the turn came from, or <see langword="null"/>.</param>
    /// <param name="ResponseId">The id minted for this turn's answer.</param>
    /// <param name="ConversationId">The id of the conversation, or <see langword="null"/> when none names it.</param>
    internal sealed record ResponsesTurn(
        AgentCoreAgent Agent,
        AgentCoreAgentSessionStore Sessions,
        AgentSession Session,
        ConversationSession Conversation,
        ChatMessage Input,
        ConversationTurnOrigin? Origin,
        string ResponseId,
        string? ConversationId)
    {
        /// <summary>
        /// Files the session, under every id that names it now. A session whose turn the store refused is never
        /// filed: the ids already name the session that saved that turn, and filing this one would replace it. One whose
        /// write merely failed is filed: nothing else holds its ids, and store 0 outranks the filed state on resume.
        /// </summary>
        public Task FileAsync(CancellationToken cancellationToken)
        {
            return Conversation.Ledger.Reads.Refused
                ? Task.CompletedTask
                : ResponsesSessionFiling.SaveAsync(
                    Sessions, Agent, Session, ResponseId, ConversationId, Conversation.ConversationId, cancellationToken);
        }
    }
}

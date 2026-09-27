using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>One turn the Responses path is about to run, and the response id the answer files under.</summary>
    /// <param name="Agent">The entry the URL named.</param>
    /// <param name="Sessions">The store the response id is filed in after the turn.</param>
    /// <param name="Session">The session the turn advances.</param>
    /// <param name="Conversation">The conversation inside <paramref name="Session"/>.</param>
    /// <param name="Input">The user words, or the approval answer, the turn runs on.</param>
    /// <param name="Origin">Where the request said the turn came from, or <see langword="null"/>.</param>
    /// <param name="ResponseId">The id minted for this turn's answer.</param>
    /// <param name="ConversationId">The id of the conversation the answer reports, which is <see cref="Conversation"/>'s own id whenever the request resumed it directly, and the resolved owner when it resumed through a response id.</param>
    internal sealed record ResponsesTurn(
        AgentCoreAgent Agent,
        AgentCoreAgentSessionStore Sessions,
        AgentSession Session,
        ConversationSession Conversation,
        ChatMessage Input,
        ConversationTurnOrigin? Origin,
        string ResponseId,
        string ConversationId)
    {
        /// <summary>
        /// Records that <see cref="ResponseId"/> continues this conversation, unless the turn's own store write
        /// was refused: the response id already names the session that saved that turn, and filing this one
        /// would misname it. A turn whose write merely failed is still recorded here: nothing else holds the
        /// response id, and store 0's own state outranks it on any later resume.
        /// </summary>
        public Task FileAsync(CancellationToken cancellationToken)
        {
            return Conversation.Ledger.Reads.Refused
                ? Task.CompletedTask
                : ResponsesSessionFiling.SaveAsync(Sessions, ResponseId, Conversation.ConversationId, cancellationToken);
        }
    }
}

using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>What a Responses request asks to run, before it holds the conversation.</summary>
    /// <param name="Agent">The entry the URL named.</param>
    /// <param name="Sessions">The store the session is filed in after the turn.</param>
    /// <param name="Session">The session the turn advances.</param>
    /// <param name="Words">The user words the turn runs on, or <see langword="null"/> for an approval answer.</param>
    /// <param name="Approval">The approval answer the turn runs on, or <see langword="null"/> for words.</param>
    /// <param name="Origin">Where the request said the turn came from, or <see langword="null"/>.</param>
    /// <param name="ConversationId">The id of the conversation, or <see langword="null"/> when none names it.</param>
    internal sealed record ResponsesPendingTurn(
        AgentCoreAgent Agent,
        AgentCoreAgentSessionStore Sessions,
        AgentSession Session,
        ChatMessage? Words,
        ResponsesApprovalAnswer? Approval,
        ConversationTurnOrigin? Origin,
        string? ConversationId);
}

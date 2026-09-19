using AgentCore.AspNetCore.Sessions;
using Microsoft.Agents.AI;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>Files a session the Responses path just advanced, under every id that names it.</summary>
internal static class ResponsesSessionFiling
{
    /// <summary>Files the session the turn just advanced, under every id that names it now.</summary>
    internal static async Task SaveAsync(
        AgentCoreAgentSessionStore sessions,
        AIAgent agent,
        AgentSession session,
        string responseId,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId is not null)
        {
            await sessions.SaveSessionAsync(agent, conversationId, session, cancellationToken).ConfigureAwait(false);
        }

        await sessions.SaveSessionAsync(agent, responseId, session, cancellationToken).ConfigureAwait(false);
    }
}

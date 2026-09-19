using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>The whole-reply branch of the Responses path: one turn, one JSON answer.</summary>
internal static class ResponsesTurnReply
{
    /// <summary>Runs one turn and answers the whole reply, filed under its ids.</summary>
    internal static async Task WriteAsync(
        HttpContext http,
        AgentCoreAgent agent,
        AgentCoreAgentSessionStore sessions,
        AgentSession session,
        ConversationSession conversation,
        ChatMessage input,
        ConversationTurnOrigin? origin,
        string responseId,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        var turn = await conversation
            .RunTurnMessageAtOriginAsync(input, origin, cancellationToken)
            .ConfigureAwait(false);

        await ResponsesSessionFiling.SaveAsync(sessions, agent, session, responseId, conversationId, cancellationToken)
            .ConfigureAwait(false);

        var response = new AgentResponse(new ChatMessage(ChatRole.Assistant, turn.ReplyText))
        {
            AgentId = agent.Id,
            ResponseId = responseId,
            CreatedAt = turn.EndedAt,
        };

        var rendered = OpenAIResponses.WriteResponse(response, responseId, conversationId);

        // The render is a closed JsonElement, so the turn facts go on as JSON: the object
        // the framework wrote, with the metadata member replaced by ours beside its own.

        var node = JsonNode.Parse(rendered.GetRawText())!.AsObject();
        JsonObject metadata = node["metadata"]?.AsObject() ?? [];

        foreach (var (name, value) in ResponsesAgentCore.TurnMetadata(conversation, turn))
        {
            metadata[name] = value;
        }

        node["metadata"] = metadata;

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers[ResponsesEndpointRouteBuilderExtensions.StageHeaderName] = turn.StageAfter;

        await http.Response
            .WriteAsJsonAsync(node, cancellationToken)
            .ConfigureAwait(false);
    }
}

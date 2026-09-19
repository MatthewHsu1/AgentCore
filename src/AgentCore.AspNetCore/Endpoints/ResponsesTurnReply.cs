using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>The whole-reply branch of the Responses path: one turn, one JSON answer.</summary>
internal static class ResponsesTurnReply
{
    /// <summary>Runs one turn and answers the whole reply, filed under its ids.</summary>
    internal static async Task WriteAsync(HttpContext http, ResponsesTurn turn, CancellationToken cancellationToken)
    {
        var result = await turn.Conversation
            .RunTurnMessageAtOriginAsync(turn.Input, turn.Origin, cancellationToken)
            .ConfigureAwait(false);

        await turn.FileAsync(cancellationToken).ConfigureAwait(false);

        var response = new AgentResponse(new ChatMessage(ChatRole.Assistant, result.ReplyText))
        {
            AgentId = turn.Agent.Id,
            ResponseId = turn.ResponseId,
            CreatedAt = result.EndedAt,
        };

        var rendered = OpenAIResponses.WriteResponse(response, turn.ResponseId, turn.ConversationId);

        // The render is a closed JsonElement, so the turn facts go on as JSON: the object
        // the framework wrote, with the metadata member replaced by ours beside its own.

        var node = JsonNode.Parse(rendered.GetRawText())!.AsObject();
        JsonObject metadata = node["metadata"]?.AsObject() ?? [];

        foreach (var (name, value) in ResponsesAgentCore.TurnMetadata(turn.Conversation, result))
        {
            metadata[name] = value;
        }

        node["metadata"] = metadata;

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers[ResponsesEndpointRouteBuilderExtensions.StageHeaderName] = result.StageAfter;

        await http.Response
            .WriteAsJsonAsync(node, cancellationToken)
            .ConfigureAwait(false);
    }
}

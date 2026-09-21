using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>The whole-reply branch of the Responses path: one turn, one JSON answer.</summary>
    internal static class ResponsesTurnReply
    {
        /// <summary>Runs one turn and answers the whole reply, filed under its ids.</summary>
        internal static async Task WriteAsync(HttpContext http, ResponsesTurn turn, CancellationToken cancellationToken)
        {
            TurnResult result = await turn.Conversation
                .RunTurnMessageAtOriginAsync(turn.Input, turn.Origin, cancellationToken)
                .ConfigureAwait(false);

            await turn.FileAsync(cancellationToken).ConfigureAwait(false);

            AgentResponse response = new(new ChatMessage(ChatRole.Assistant, result.ReplyText))
            {
                AgentId = turn.Agent.Id,
                ResponseId = turn.ResponseId,
                CreatedAt = result.EndedAt,
            };

            JsonElement rendered = OpenAIResponses.WriteResponse(response, turn.ResponseId, turn.ConversationId);

            // The render is a closed JsonElement, so the turn facts go on as JSON: the object
            // the framework wrote, with the metadata member replaced by ours beside its own.

            JsonObject node = JsonNode.Parse(rendered.GetRawText())!.AsObject();
            JsonObject metadata = node["metadata"]?.AsObject() ?? [];

            foreach ((string? name, string? value) in ResponsesAgentCore.TurnMetadata(turn.Conversation, result))
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
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Conversation;
using AgentCore.Application.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The streaming half of the Responses path: one turn as server-sent events, with the dialect's
/// browser parts riding beside the framework's frames.
/// </summary>
internal static class ResponsesTurnStream
{
    /// <summary>Runs one turn and writes one Responses event for each update, filed under its ids.</summary>
    /// <remarks>
    /// The session is filed after the enumeration ends, because the turn commits —
    /// and the session only holds the turn — once the last update has left it.
    /// The framework's frames carry text alone; when the request spoke the dialect,
    /// each update's browser parts ride beside them as <c>agentcore_*</c> members, so drawings,
    /// citations, tool halves, and approval asks stream.
    /// </remarks>
    internal static async Task WriteAsync(
        HttpContext http,
        ResponsesTurn turn,
        bool dialect,
        CancellationToken cancellationToken)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        // The headers leave before the turn ends, so this one names the stage the turn speaks in.
        http.Response.Headers[ResponsesEndpointRouteBuilderExtensions.StageHeaderName] = turn.Conversation.Stage;

        // One turn's worth of ids: the pairing dies with the stream.
        ToolCallNames toolNames = new();

        var updates = StreamAgentUpdatesAsync(http, turn, dialect, toolNames, cancellationToken);

        await foreach (var frame in OpenAIResponses
            .WriteResponseStreamAsync(updates, turn.ResponseId, turn.ConversationId, cancellationToken)
            .ConfigureAwait(false))
        {
            await http.Response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

            // Without this the reply arrives in one piece, which defeats the whole streaming path.
            await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await turn.FileAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one streaming turn on the conversation, wrapping each update with the agent's id.</summary>
    /// <remarks>
    /// The framework converter sees every update untouched: it ignores the contents it
    /// knows nothing of, so dialect lines duplicate nothing and pure streams lose nothing.
    /// </remarks>
    private static async IAsyncEnumerable<AgentResponseUpdate> StreamAgentUpdatesAsync(
        HttpContext http,
        ResponsesTurn turn,
        bool dialect,
        ToolCallNames toolNames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        TurnStreamFiles files = new();

        await foreach (var update in turn.Conversation
            .RunTurnMessageStreamingAtOriginAsync(turn.Input, turn.Origin, cancellationToken)
            .ConfigureAwait(false))
        {
            if (dialect)
            {
                foreach (var part in TurnStreamParts.From(update, toolNames))
                {
                    await WritePartLineAsync(http, part, cancellationToken).ConfigureAwait(false);
                }

                files.Note(update);
            }

            yield return new AgentResponseUpdate(update) { AgentId = turn.Agent.Id };
        }

        if (dialect)
        {
            // The enumeration above ends only after the run has finished, and the publish tool ran
            // inside it: by here the bytes are in the store or never will be.
            var conversations = http.RequestServices.GetRequiredService<Conversations>();

            await foreach (var part in files.ResolveAsync(conversations, turn.Conversation.ConversationId, cancellationToken).ConfigureAwait(false))
            {
                await WritePartLineAsync(http, part, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Writes one dialect event: one <c>agentcore_*</c> member, bare.</summary>
    private static async Task WritePartLineAsync(
        HttpContext http, TurnStreamPart part, CancellationToken cancellationToken)
    {
        JsonObject line = part switch
        {
            TurnStreamRender render
                => new JsonObject { ["agentcore_data"] = JsonSerializer.SerializeToNode(render.Payload, ResponsesJson.Options) },
            TurnStreamSource source
                => new JsonObject { ["agentcore_source"] = JsonSerializer.SerializeToNode(source.Payload, ResponsesJson.Options) },
            TurnStreamTool tool
                => new JsonObject { ["agentcore_tool"] = JsonSerializer.SerializeToNode(tool.Payload, ResponsesJson.Options) },
            TurnStreamApproval approval
                => new JsonObject { ["agentcore_approval"] = JsonSerializer.SerializeToNode(approval.Payload, ResponsesJson.Options) },
            TurnStreamFile file
                => new JsonObject { ["agentcore_file"] = JsonSerializer.SerializeToNode(file.Payload, ResponsesJson.Options) },
            _ => throw new InvalidOperationException($"Unknown stream part: {part.GetType()}."),
        };

        await http.Response.WriteAsync("data: ", cancellationToken).ConfigureAwait(false);
        await http.Response.WriteAsync(line.ToJsonString(), cancellationToken).ConfigureAwait(false);
        await http.Response.WriteAsync("\n\n", cancellationToken).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

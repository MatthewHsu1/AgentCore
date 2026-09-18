using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Calls;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.Sessions;
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
        AgentCoreAgent agent,
        AgentCoreAgentSessionStore sessions,
        AgentSession session,
        CallSession call,
        ChatMessage input,
        CallTurnOrigin? origin,
        string responseId,
        string? conversationId,
        bool dialect,
        CancellationToken cancellationToken)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        // The headers leave before the turn ends, so this one names the stage the turn speaks in.
        http.Response.Headers[ResponsesEndpointRouteBuilderExtensions.StageHeaderName] = call.Stage;

        // One turn's worth of ids: the pairing dies with the stream.
        ToolCallNames toolNames = new();

        var updates = StreamAgentUpdatesAsync(http, agent, call, input, origin, dialect, toolNames, cancellationToken);
        
        await foreach (var frame in OpenAIResponses
            .WriteResponseStreamAsync(updates, responseId, conversationId, cancellationToken)
            .ConfigureAwait(false))
        {
            await http.Response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

            // Without this the reply arrives in one piece, which defeats the whole streaming path.
            await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await ResponsesSessionFiling.SaveAsync(sessions, agent, session, responseId, conversationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs one streaming turn on the call, wrapping each update with the agent's id.</summary>
    /// <remarks>
    /// The framework converter sees every update untouched: it ignores the contents it
    /// knows nothing of, so dialect lines duplicate nothing and pure streams lose nothing.
    /// </remarks>
    private static async IAsyncEnumerable<AgentResponseUpdate> StreamAgentUpdatesAsync(
        HttpContext http,
        AgentCoreAgent agent,
        CallSession call,
        ChatMessage input,
        CallTurnOrigin? origin,
        bool dialect,
        ToolCallNames toolNames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        TurnStreamFiles files = new();

        await foreach (var update in call
            .RunTurnMessageStreamingAtOriginAsync(input, origin, cancellationToken)
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

            yield return new AgentResponseUpdate(update) { AgentId = agent.Id };
        }

        if (dialect)
        {
            // The enumeration above ends only after the run's providers have finished, and the
            // capture provider is one of them: by here the bytes are in the store or never will be.
            var calls = http.RequestServices.GetRequiredService<CallRepository>();

            await foreach (var part in files.ResolveAsync(calls, call.CallId, cancellationToken).ConfigureAwait(false))
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

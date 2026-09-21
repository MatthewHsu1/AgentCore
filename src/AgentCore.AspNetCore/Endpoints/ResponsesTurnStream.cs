using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Conversation;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The streaming half of the Responses path: one turn as server-sent events, with the dialect's
    /// browser parts riding beside the framework's frames.
    /// </summary>
    internal static class ResponsesTurnStream
    {
        /// <summary>Runs one turn and writes one Responses event for each update, filed under its ids.</summary>
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

            IAsyncEnumerable<AgentResponseUpdate> updates = StreamAgentUpdatesAsync(http, turn, dialect, toolNames, cancellationToken);

            await foreach (string? frame in OpenAIResponses
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
        private static async IAsyncEnumerable<AgentResponseUpdate> StreamAgentUpdatesAsync(
            HttpContext http,
            ResponsesTurn turn,
            bool dialect,
            ToolCallNames toolNames,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            TurnStreamFiles files = new();

            await foreach (ChatResponseUpdate? update in turn.Conversation
                .RunTurnMessageStreamingAtOriginAsync(turn.Input, turn.Origin, cancellationToken)
                .ConfigureAwait(false))
            {
                bool isNotice = update.Contents.OfType<NoticeContent>().Any();

                if (dialect)
                {
                    foreach (TurnStreamPart part in TurnStreamParts.From(update, toolNames))
                    {
                        await WritePartLineAsync(http, part, cancellationToken).ConfigureAwait(false);
                    }

                    if (!isNotice)
                    {
                        files.Note(update);
                    }
                }

                if (isNotice)
                {
                    continue;
                }

                yield return new AgentResponseUpdate(update) { AgentId = turn.Agent.Id };
            }

            if (dialect)
            {
                // The enumeration above ends only after the run has finished, and the publish tool ran
                // inside it: by here the bytes are in the store or never will be.
                Conversations conversations = http.RequestServices.GetRequiredService<Conversations>();

                await foreach (TurnStreamPart part in files.ResolveAsync(conversations, turn.Conversation.ConversationId, cancellationToken).ConfigureAwait(false))
                {
                    await WritePartLineAsync(http, part, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Writes one dialect event: one <c>agentcore_*</c> member, bare.</summary>
        private static async Task WritePartLineAsync(
            HttpContext http, TurnStreamPart part, CancellationToken cancellationToken)
        {
            JsonObject line = new()
            {
                [part.Member] = JsonSerializer.SerializeToNode(part.Payload, part.Payload.GetType(), ResponsesJson.Options),
            };

            await http.Response.WriteAsync("data: ", cancellationToken).ConfigureAwait(false);
            await http.Response.WriteAsync(line.ToJsonString(), cancellationToken).ConfigureAwait(false);
            await http.Response.WriteAsync("\n\n", cancellationToken).ConfigureAwait(false);
            await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

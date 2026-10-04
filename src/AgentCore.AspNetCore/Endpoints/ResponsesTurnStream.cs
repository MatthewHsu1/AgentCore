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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgentCore.Application.Runtime.Turn.Lifecycle;

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
            // Started before the headers: only a started turn has opened its stored state and caught up on
            // other sessions', so only then does the stage name the one the turn speaks in.
            await using TurnRun run = await turn.Conversation
                .StartTurnAsync(turn.Input, turn.Origin, cancellationToken).ConfigureAwait(false);

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers[ResponsesEndpointRouteBuilderExtensions.StageHeaderName] = turn.Conversation.Stage;

            // One turn's worth of ids: the pairing dies with the stream.
            ToolCallNames toolNames = new();

            IAsyncEnumerable<AgentResponseUpdate> updates = StreamAgentUpdatesAsync(http, turn, run, dialect, toolNames, cancellationToken);
            string? lastFrame = null;

            try
            {
                await foreach (string? frame in OpenAIResponses
                    .WriteResponseStreamAsync(updates, turn.ResponseId, turn.ConversationId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await http.Response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

                    // Without this the reply arrives in one piece, which defeats the whole streaming path.
                    await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    lastFrame = frame;
                }
            }
            catch (ConversationTurnConflictException) when (!cancellationToken.IsCancellationRequested)
            {
                // The reply already streamed, so the refusal can only end the stream, never set its status.
                await ResponsesTurnConflict.WriteEventAsync(http, lastFrame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The turn commits its words even when the host cancels mid-stream, so the
                // session still needs filing here, on a token that outlives the abort that cancelled the one above.
                if (cancellationToken.IsCancellationRequested)
                {
                    await FileAfterAbortAsync(
                            turn.FileAsync,
                            turn.Conversation.ConversationId,
                            turn.Conversation.Time,
                            http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ResponsesTurnStream))
                                ?? NullLogger.Instance)
                        .ConfigureAwait(false);
                }
                else
                {
                    await turn.FileAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Files the session of a turn whose client has gone, within
        /// <see cref="TurnFailureReasons.CompletionTimeout"/>: the same bound the rest of the work after
        /// the reply runs under once the host's token no longer counts.
        /// </summary>
        /// <param name="file">Files the session; it reads the bound's token.</param>
        /// <param name="conversationId">The conversation being filed, for the log.</param>
        /// <param name="time">The clock the bound runs on.</param>
        /// <param name="logger">Where a filing that outruns the bound is reported.</param>
        internal static async Task FileAfterAbortAsync(
            Func<CancellationToken, Task> file, string conversationId, TimeProvider time, ILogger logger)
        {
            TimeSpan bound = TurnFailureReasons.CompletionTimeout;
            using CancellationTokenSource deadline = new(bound, time);

            try
            {
                await file(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                ResponsesTurnLog.FilingAfterAbortTimedOut(logger, conversationId, bound);
            }
        }

        /// <summary>Reads one started turn's reply, wrapping each update with the agent's id.</summary>
        private static async IAsyncEnumerable<AgentResponseUpdate> StreamAgentUpdatesAsync(
            HttpContext http,
            ResponsesTurn turn,
            TurnRun run,
            bool dialect,
            ToolCallNames toolNames,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            TurnStreamFiles files = new();

            await foreach (ChatResponseUpdate? update in run.Updates.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                bool isNotice = update.Contents.OfType<NoticeContent>().Any();

                // Carries no text of its own: the caller reads it off the agentcore_message_committed
                // dialect part instead, never as spoken or output text.
                bool isCommitted = update.Contents.OfType<TurnCommittedContent>().Any();

                if (dialect)
                {
                    foreach (TurnStreamPart part in TurnStreamParts.From(update, toolNames))
                    {
                        await WritePartLineAsync(http, part, cancellationToken).ConfigureAwait(false);
                    }

                    if (!isNotice && !isCommitted)
                    {
                        files.Note(update);
                    }
                }

                if (isNotice || isCommitted)
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

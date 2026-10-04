using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The Responses path: an OpenAI-compatible <c>POST /v1/{entry}/responses</c> over the turn loop,
    /// with the conversation continuing across requests through the session store.
    /// </summary>
    public static class ResponsesEndpointRouteBuilderExtensions
    {
        /// <summary>The route parameter that names the entry, as <c>{entry}</c> in a pattern.</summary>
        public const string EntryRouteParameter = "entry";

        /// <summary>The route every entry answers on when the host names none.</summary>
        public const string DefaultPattern = "/v1/{" + EntryRouteParameter + "}/responses";

        /// <summary>The answer header that reports the stage the machine holds.</summary>
        public const string StageHeaderName = "X-AgentCore-Stage";

        /// <summary>
        /// The request header that names the zone the person is in, as an IANA id such as
        /// <c>America/Chicago</c>.
        /// </summary>
        public const string TimeZoneHeaderName = "X-AgentCore-Time-Zone";

        /// <summary>Maps every entry on <see cref="DefaultPattern"/>, with the URL naming the entry.</summary>
        /// <param name="endpoints">The route builder of the host.</param>
        /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
        public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints)
        {
            return endpoints.MapResponses(DefaultPattern);
        }

        /// <summary>Maps every entry on one route.</summary>
        /// <param name="endpoints">The route builder of the host.</param>
        /// <param name="pattern">
        /// The route to answer on. It carries the <c>{entry}</c> parameter, or a hook's
        /// <see cref="AgentCore.Application.Hooks.AgentHook.BeforeEntryAsync"/> picks the entry. Startup fails when it has neither.
        /// </param>
        /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
        public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints, string pattern)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrEmpty(pattern);

            return endpoints.MapPost(pattern, HandleAsync).WithMetadata(AgentCoreRouteMetadata.Http);
        }

        /// <summary>Runs one turn of one conversation, and files the session under the ids the answer carries.</summary>
        private static async Task HandleAsync(HttpContext http)
        {
            CancellationToken cancellationToken = http.RequestAborted;
            if (await AgentCoreEntries.ResolveAsync(http, cancellationToken).ConfigureAwait(false) is not { } entry)
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status403Forbidden,
                    "this route runs no entry for this caller.",
                    "entry_refused",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (await ResponsesRequestReader.ReadBodyAsync(http, cancellationToken).ConfigureAwait(false) is not { } body)
            {
                return;
            }

            ResponsesRequestInfo? agentcore = ResponsesAgentCore.ReadRequest(body);
            ResponsesApprovalAnswer? approval = agentcore?.Approval;

            ResponsesRunInput? run = await ResponsesRequestReader
                .ReadRunInputAsync(http, body, approving: approval is not null, cancellationToken)
                .ConfigureAwait(false);

            if (run is null)
            {
                return;
            }

            EntryRegistry entries = http.RequestServices.GetRequiredService<AgentCoreBoot>().Entries;
            if (!entries.Agents.TryGetValue(entry, out AgentCoreAgent? agent))
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    EntryRegistry.UnknownEntryMessage(entry, entries.Entries),
                    "unknown_entry",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            AgentCoreAgentSessionStore sessions = http.RequestServices.GetRequiredService<AgentCoreAgentSessionStore>();

            (AgentSession Session, string ConversationId)? opened = await OpenSessionAsync(http, agent, sessions, run, approving: approval is not null, cancellationToken)
                .ConfigureAwait(false);

            if (opened is null)
            {
                return;
            }

            (AgentSession session, string conversationId) = opened.Value;

            if (!await CheckInputAsync(http, approval, run.Messages, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // A conversation the request named stays the conversation; a keyless first turn minted
            // one alongside the session above. A response id is minted every turn either way, so a
            // chain can start from any answer.
            ResponsesPendingTurn pending = new(
                agent,
                sessions,
                session,
                approval is null ? ResponsesRequestReader.LastUserMessage(run.Messages) : null,
                approval,
                ResponsesAgentCore.OriginOf(agentcore),
                conversationId);

            await RunTurnAsync(http, body, pending, dialect: agentcore is not null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Opens the session the turn runs on: a new one, or the one the continuation names.</summary>
        /// <returns>
        /// The session and the conversation id it runs under, or <see langword="null"/> once a
        /// refusal has been answered.
        /// </returns>
        private static async Task<(AgentSession Session, string ConversationId)?> OpenSessionAsync(
            HttpContext http,
            AgentCoreAgent agent,
            AgentCoreAgentSessionStore sessions,
            ResponsesRunInput run,
            bool approving,
            CancellationToken cancellationToken)
        {
            string? key = run.ContinuationKey;
            string? conversationId = run.ConversationId;

            string? resolved = key is null
                ? null
                : await sessions.ResolveConversationIdAsync(key, cancellationToken).ConfigureAwait(false);
            bool known = resolved is not null;

            bool namesNewConversation = key is not null
                && !known
                && string.Equals(key, conversationId, StringComparison.Ordinal)
                && conversationId is not null
                && !approving;

            try
            {
                if (key is null)
                {
                    if (approving)
                    {
                        await ResponsesRequestReader.WriteErrorAsync(
                            http,
                            StatusCodes.Status400BadRequest,
                            "an approval answer names the conversation it resumes. Send it with the conversation "
                            + "or previous response id the asking turn answered with.",
                            "missing_session",
                            cancellationToken).ConfigureAwait(false);
                        return null;
                    }

                    conversationId = ResponsesRequestReader.NewConversationId();
                    return (await agent.CreateSessionAsync(conversationId, cancellationToken).ConfigureAwait(false), conversationId);
                }

                if (namesNewConversation)
                {
                    return (await agent.CreateSessionAsync(key, cancellationToken).ConfigureAwait(false), conversationId!);
                }

                if (!known)
                {
                    await ResponsesRequestReader.WriteErrorAsync(
                        http,
                        StatusCodes.Status404NotFound,
                        $"no conversation opens under '{key}'. Send the request with neither a conversation nor a "
                        + "previous response id to start one.",
                        "continuation_not_found",
                        cancellationToken).ConfigureAwait(false);
                    return null;
                }

                return (await sessions.GetSessionForConversationAsync(agent, resolved!, cancellationToken).ConfigureAwait(false), resolved!);
            }
            catch (ConversationInUseException failure)
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status409Conflict,
                    failure.Message,
                    "conversation_in_use",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (InvalidOperationException failure)
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    failure.Message,
                    "continuation_not_found",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        /// <summary>Checks the request carries one thing to run on: user words, or an approval answer without words.</summary>
        /// <returns><see langword="false"/> once a refusal has been answered.</returns>
        private static async Task<bool> CheckInputAsync(
            HttpContext http,
            ResponsesApprovalAnswer? approval,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken)
        {
            if (approval is null && ResponsesRequestReader.LastUserMessage(messages) is null)
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request carries no user message with text, so there is no turn to run.",
                    "no_user_message",
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (approval is not null && ResponsesRequestReader.LastUserText(messages) is { Length: > 0 })
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request carries both a user message and an approval answer. One turn carries "
                    + "either words or an answer, never both.",
                    "mixed_turn",
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the approval answer off the conversation's queue, once the request holds the conversation, so no other
        /// turn changes the queue between this read and the turn.
        /// </summary>
        /// <returns>The answer, or <see langword="null"/> once a refusal has been answered.</returns>
        private static async Task<ChatMessage?> ReadApprovalAsync(
            HttpContext http,
            ConversationSession conversation,
            ResponsesApprovalAnswer approval,
            CancellationToken cancellationToken)
        {
            // Async: a resumed conversation has run no turn in this process, so its ledger session —
            // and the queue the suspending turn filed — is still closed. This opens it first.
            if (await conversation.TryCreateApprovalAnswerAsync(approval.RequestId, approval.Approved, cancellationToken).ConfigureAwait(false) is { } answer)
            {
                return answer;
            }

            await ResponsesRequestReader.WriteErrorAsync(
                http,
                StatusCodes.Status409Conflict,
                $"the conversation queues no approval under id '{approval.RequestId}'. It was "
                + "answered already, belongs to another conversation, or was never asked.",
                "no_pending_approval",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        /// <summary>
        /// Runs the turn on the branch the body asked for, once the conversation is free, and answers a refused turn as
        /// 409.
        /// </summary>
        private static async Task RunTurnAsync(
            HttpContext http,
            JsonElement body,
            ResponsesPendingTurn pending,
            bool dialect,
            CancellationToken cancellationToken)
        {
            bool streaming = body.TryGetProperty("stream", out JsonElement streamFlag)
                && streamFlag.ValueKind == JsonValueKind.True;

            string refusedReason = TurnRefusalTokens.ToToken(TurnRefusal.Busy);
            try
            {
                // The conversation stays busy until the session is filed, after the abort too: a message sent at once,
                // to this host or another, then waits for this turn's words instead of racing them to the same turn index.
                ConversationSession conversation = await pending.Agent
                    .EnterRequestAsync(pending.Session, cancellationToken).ConfigureAwait(false);
                refusedReason = TurnRefusalTokens.ToToken(TurnRefusal.Conflict);
                try
                {
                    ChatMessage? input = pending.Approval is { } approval
                        ? await ReadApprovalAsync(http, conversation, approval, cancellationToken).ConfigureAwait(false)
                        : pending.Words;

                    if (input is null)
                    {
                        return;
                    }

                    if (CallerTimeZone.Parse(http.Request.Headers[TimeZoneHeaderName]) is { } zone)
                    {
                        CallerTimeZone.Set(pending.Session, zone);
                    }

                    ResponsesTurn turn = new(
                        pending.Agent,
                        pending.Sessions,
                        pending.Session,
                        conversation,
                        input,
                        pending.Origin,
                        OpenAIResponses.CreateResponseId(),
                        pending.ConversationId);

                    if (streaming)
                    {
                        // The dialect is opt-in by the member only our clients send: an OpenAI SDK
                        // never carries agentcore, so its stream stays the framework's pure shapes.
                        await ResponsesTurnStream.WriteAsync(http, turn, dialect, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ResponsesTurnReply.WriteAsync(http, turn, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await conversation.Busy.ExitRequestAsync().ConfigureAwait(false);
                }
            }
            catch (ConversationTurnConflictException) when (!http.Response.HasStarted)
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status409Conflict,
                    ResponsesTurnConflict.MessageFor(refusedReason),
                    ResponsesTurnConflict.Code,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ConversationTurnConflictException)
            {
                // The stream already ended with its error event, or the client that could read one is gone.
            }
            catch (InvalidOperationException exception)
            {
                // The turn loop refuses a turn on a finished conversation, or on one disposed under it. Neither is a
                // defect of this host.
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status409Conflict,
                    exception.Message,
                    "turn_refused",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

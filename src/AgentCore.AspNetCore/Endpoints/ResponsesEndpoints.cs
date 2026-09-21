using System.Text.Json;
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

        /// <summary>Maps every entry on one route, with the URL naming the entry.</summary>
        /// <param name="endpoints">The route builder of the host.</param>
        /// <param name="pattern">The route to answer on. It must carry the <c>{entry}</c> parameter.</param>
        /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
        /// <exception cref="ArgumentException"><paramref name="pattern"/> carries no <c>{entry}</c>.</exception>
        public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints, string pattern)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrEmpty(pattern);

            return !pattern.Contains("{" + EntryRouteParameter + "}", StringComparison.Ordinal)
                ? throw new ArgumentException(
                    $"The pattern '{pattern}' carries no {{{EntryRouteParameter}}} parameter, so no URL can name "
                    + "an entry.",
                    nameof(pattern))
                : endpoints.MapPost(pattern, HandleAsync);
        }

        /// <summary>Runs one turn of one conversation, and files the session under the ids the answer carries.</summary>
        /// <remarks>
        /// Every step answers its own refusal and returns <see langword="null"/>, so this reads as
        /// the happy path alone.
        /// </remarks>
        private static async Task HandleAsync(HttpContext http)
        {
            CancellationToken cancellationToken = http.RequestAborted;
            string entry = http.Request.RouteValues[EntryRouteParameter] as string ?? string.Empty;

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

            (AgentSession Session, string? ConversationId)? opened = await OpenSessionAsync(http, agent, sessions, run, approving: approval is not null, cancellationToken)
                .ConfigureAwait(false);

            if (opened is null)
            {
                return;
            }

            (AgentSession? session, string? conversationId) = opened.Value;

            if (session.GetService<ConversationSession>() is not { } conversation)
            {
                throw new InvalidOperationException(
                    "The session is not one this agent created, so it names no call to run.");
            }

            ChatMessage? input = await ReadInputAsync(http, conversation, approval, run.Messages, cancellationToken)
                .ConfigureAwait(false);

            if (input is null)
            {
                return;
            }

            // A conversation the request named stays the conversation; a keyless first turn minted
            // one alongside the session above. A response id is minted every turn either way, so a
            // chain can start from any answer.
            ResponsesTurn turn = new(
                agent,
                sessions,
                session,
                conversation,
                input,
                ResponsesAgentCore.OriginOf(agentcore),
                OpenAIResponses.CreateResponseId(),
                conversationId);

            await RunTurnAsync(http, body, turn, dialect: agentcore is not null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Opens the session the turn runs on: a new one, or the one the continuation names.</summary>
        /// <returns>
        /// The session and the conversation id it runs under, or <see langword="null"/> once a
        /// refusal has been answered.
        /// </returns>
        private static async Task<(AgentSession Session, string? ConversationId)?> OpenSessionAsync(
            HttpContext http,
            AgentCoreAgent agent,
            AgentCoreAgentSessionStore sessions,
            ResponsesRunInput run,
            bool approving,
            CancellationToken cancellationToken)
        {
            string? key = run.ContinuationKey;
            string? conversationId = run.ConversationId;

            bool known = key is not null
                && await sessions.ContainsAsync(key, cancellationToken).ConfigureAwait(false);

            bool namesNewConversation = key is not null
                && !known
                && string.Equals(key, conversationId, StringComparison.Ordinal)
                && conversationId is not null
                && !approving;

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
                return (await agent.CreateSessionAsync(conversationId).ConfigureAwait(false), conversationId);
            }

            if (namesNewConversation)
            {
                // Unknown conversation with words: start the conversation under that id, so the conversation
                // and the continuation key are one id. Unknown response ids stay 404 below: they name
                // a turn, not a conversation.
                return (await agent.CreateSessionAsync(key).ConfigureAwait(false), conversationId);
            }

            if (!known)
            {
                // Either an unknown response id — a turn no answer ever carried — or an unknown
                // conversation beside an approval-only body the parse refused. A conversation with words
                // reaches the namesNewConversation branch; an approval never starts a conversation.
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    $"no conversation opens under '{key}'. Send the request with neither a conversation nor a "
                    + "previous response id to start one.",
                    "continuation_not_found",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            try
            {
                return (await sessions.GetSessionAsync(agent, key, cancellationToken).ConfigureAwait(false), conversationId);
            }
            catch (InvalidOperationException failure)
            {
                // A key minted by another entry is unknown to this one, never a cross-entry read.
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    failure.Message,
                    "continuation_not_found",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        /// <summary>Picks what the turn runs on: the approval answer, or the last user message with words.</summary>
        /// <returns>The input, or <see langword="null"/> once a refusal has been answered.</returns>
        private static async Task<ChatMessage?> ReadInputAsync(
            HttpContext http,
            ConversationSession conversation,
            ResponsesApprovalAnswer? approval,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken)
        {
            if (approval is null)
            {
                if (ResponsesRequestReader.LastUserMessage(messages) is { } user)
                {
                    return user;
                }

                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request carries no user message with text, so there is no turn to run.",
                    "no_user_message",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (ResponsesRequestReader.LastUserText(messages) is { Length: > 0 })
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request carries both a user message and an approval answer. One turn carries "
                    + "either words or an answer, never both.",
                    "mixed_turn",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

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

        /// <summary>Runs the turn on the branch the body asked for, and answers a refused turn as 409.</summary>
        private static async Task RunTurnAsync(
            HttpContext http,
            JsonElement body,
            ResponsesTurn turn,
            bool dialect,
            CancellationToken cancellationToken)
        {
            bool streaming = body.TryGetProperty("stream", out JsonElement streamFlag)
                && streamFlag.ValueKind == JsonValueKind.True;

            if (CallerTimeZone.Parse(http.Request.Headers[TimeZoneHeaderName]) is { } zone)
            {
                CallerTimeZone.Set(turn.Session, zone);
            }

            try
            {
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
            catch (InvalidOperationException exception)
            {
                // The turn loop refuses a turn on a finished conversation, and refuses a second turn while one
                // runs. Both are a caller mistake and neither is a defect of this host.
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

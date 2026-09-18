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

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The Responses path: an OpenAI-compatible <c>POST /v1/{entry}/responses</c> over the turn loop,
/// with the call continuing across requests through the session store.
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

    /// <summary>The OpenAI error type every refused request reports.</summary>
    private const string InvalidRequestError = "invalid_request_error";

    /// <summary>Maps every entry on <see cref="DefaultPattern"/>, with the URL naming the entry.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints)
        => endpoints.MapResponses(DefaultPattern);

    /// <summary>Maps every entry on one route, with the URL naming the entry.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on. It must carry the <c>{entry}</c> parameter.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> carries no <c>{entry}</c>.</exception>
    public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        if (!pattern.Contains("{" + EntryRouteParameter + "}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The pattern '{pattern}' carries no {{{EntryRouteParameter}}} parameter, so no URL can name "
                + "an entry.",
                nameof(pattern));
        }

        return endpoints.MapPost(pattern, HandleAsync);
    }

    /// <summary>Runs one turn of one call, and files the session under the ids the answer carries.</summary>
    private static async Task HandleAsync(HttpContext http)
    {
        var cancellationToken = http.RequestAborted;
        var entry = http.Request.RouteValues[EntryRouteParameter] as string ?? string.Empty;

        JsonElement body;
        try
        {
            body = await JsonSerializer
                .DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await ResponsesRequestReader.WriteErrorAsync(
                http,
                StatusCodes.Status400BadRequest,
                "the request body is not well-formed JSON: " + exception.Message,
                InvalidRequestError,
                "malformed_body",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var agentcore = ResponsesAgentCore.ReadRequest(body);

        OpenAIResponsesRunRequest? runRequest;

        string? conversationId;

        IReadOnlyList<ChatMessage> messages;

        try
        {
            runRequest = OpenAIResponses.ToAgentRunRequest(body);
            conversationId = runRequest.ConversationId;
            messages = [.. runRequest.Messages];
        }
        catch (ArgumentException exception)
        {
            // An approval answer carries no words, only the request it answers — and the
            // protocol parse refuses a turn with no input. The continuation still names the
            // call, so read it off the body and run the answer alone.
            if (agentcore?.Approval is null)
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request is not a Responses request: " + exception.Message,
                    InvalidRequestError,
                    "invalid_body",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            runRequest = null;
            messages = [];
            conversationId = ResponsesRequestReader.ReadConversationId(body);
        }

        var entries = http.RequestServices.GetRequiredService<AgentCoreBoot>().Entries;
        if (!entries.Agents.TryGetValue(entry, out var agent))
        {
            await ResponsesRequestReader.WriteErrorAsync(
                http,
                StatusCodes.Status404NotFound,
                EntryRegistry.UnknownEntryMessage(entry, entries.Entries),
                InvalidRequestError,
                "unknown_entry",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var sessions = http.RequestServices.GetRequiredService<AgentCoreAgentSessionStore>();

        // The continuation this turn hangs off, or null for the first turn of a call.
        // A conversation id names the call itself; a response id names one past turn of it.
        // The helper prefers the response chain over the conversation, exactly as the
        // protocol treats them as mutually exclusive; the refused-parse fallback reads
        // the same precedence off the body.
        var key = runRequest is not null
            ? OpenAIResponses.GetSessionStoreId(runRequest)
            : ResponsesRequestReader.ReadContinuationId(body);

        if (string.IsNullOrWhiteSpace(key))
        {
            key = null;
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            conversationId = null;
        }

        var known = key is not null
            && await sessions.ContainsAsync(key, cancellationToken).ConfigureAwait(false);

        var namesNewCall = key is not null
            && !known
            && string.Equals(key, conversationId, StringComparison.Ordinal)
            && conversationId is not null
            && agentcore?.Approval is null;

        AgentSession session;
        if (key is null)
        {
            if (agentcore?.Approval is not null)
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "an approval answer names the call it resumes. Send it with the conversation "
                    + "or previous response id the asking turn answered with.",
                    InvalidRequestError,
                    "missing_session",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            conversationId = ResponsesRequestReader.NewConversationId();
            session = await agent.CreateSessionAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }
        else if (namesNewCall)
        {
            // Unknown conversation with words: start the call under that id, so the conversation,
            // the call, and the continuation key are one id. Unknown response ids stay 404 below:
            // they name a turn, not a call.
            session = await agent.CreateSessionAsync(key, cancellationToken).ConfigureAwait(false);
        }
        else if (known)
        {
            try
            {
                session = await sessions.GetSessionAsync(agent, key, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException failure)
            {
                // A key minted by another entry is unknown to this one, never a cross-entry read.
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    failure.Message,
                    InvalidRequestError,
                    "continuation_not_found",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            // Either an unknown response id — a turn no answer ever carried — or an unknown
            // conversation beside an approval-only body the parse refused. A conversation with words
            // reaches the namesNewCall branch; an approval never starts a call.
            await ResponsesRequestReader.WriteErrorAsync(
                http,
                StatusCodes.Status404NotFound,
                $"no call opens under '{key}'. Send the request with neither a conversation nor a "
                + "previous response id to start one.",
                InvalidRequestError,
                "continuation_not_found",
                cancellationToken).ConfigureAwait(false);

            return;
        }

        if (session.GetService<CallSession>() is not { } call)
        {
            throw new InvalidOperationException(
                "The session is not one this agent created, so it names no call to run.");
        }

        ChatMessage input;
        if (agentcore?.Approval is { } approval)
        {
            if (ResponsesRequestReader.LastUserText(messages) is { Length: > 0 })
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request carries both a user message and an approval answer. One turn carries "
                    + "either words or an answer, never both.",
                    InvalidRequestError,
                    "mixed_turn",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            // Async: a resumed call has run no turn in this process, so its ledger session —
            // and the queue the suspending turn filed — is still closed. This opens it first.
            if (await call.TryCreateApprovalAnswerAsync(approval.RequestId, approval.Approved, cancellationToken).ConfigureAwait(false) is not { } answer)
            {
                await ResponsesRequestReader.WriteErrorAsync(
                    http,
                    StatusCodes.Status409Conflict,
                    $"the call queues no approval under id '{approval.RequestId}'. It was "
                    + "answered already, belongs to another call, or was never asked.",
                    InvalidRequestError,
                    "no_pending_approval",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            input = answer;
        }
        else if (ResponsesRequestReader.LastUserMessage(messages) is not { } user)
        {
            await ResponsesRequestReader.WriteErrorAsync(
                http,
                StatusCodes.Status400BadRequest,
                "the request carries no user message with text, so there is no turn to run.",
                InvalidRequestError,
                "no_user_message",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        else
        {
            input = user;
        }

        var origin = ResponsesAgentCore.OriginOf(agentcore);

        var streaming = body.TryGetProperty("stream", out var streamFlag)
            && streamFlag.ValueKind == JsonValueKind.True;

        // A conversation the request named stays the conversation; a keyless first turn minted
        // one alongside the session above. A response id is minted every turn either way, so a
        // chain can start from any answer.
        var responseId = OpenAIResponses.CreateResponseId();

        // Only the stream has a frame to carry a drawing, so only the stream gets a screen. Binding
        // one on the whole-reply branch would let the tool report a picture the caller never sees.
        // Set per request, not once: the session outlives a request and the branch can differ per turn.
        call.SetHasScreen(streaming);

        if (CallerTimeZone.Parse(http.Request.Headers[TimeZoneHeaderName]) is { } zone)
        {
            CallerTimeZone.Set(session, zone);
        }

        try
        {
            if (streaming)
            {
                // The dialect is opt-in by the member only our clients send: an OpenAI SDK
                // never carries agentcore, so its stream stays the framework's pure shapes.
                await ResponsesTurnStream.WriteAsync(http, agent, sessions, session, call, input, origin, responseId, conversationId, agentcore is not null, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ResponsesTurnReply.WriteAsync(http, agent, sessions, session, call, input, origin, responseId, conversationId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException exception)
        {
            // The turn loop refuses a turn on a finished call, and refuses a second turn while one
            // runs. Both are a caller mistake and neither is a defect of this host.
            await ResponsesRequestReader.WriteErrorAsync(
                http,
                StatusCodes.Status409Conflict,
                exception.Message,
                InvalidRequestError,
                "turn_refused",
                cancellationToken).ConfigureAwait(false);
        }
    }
}

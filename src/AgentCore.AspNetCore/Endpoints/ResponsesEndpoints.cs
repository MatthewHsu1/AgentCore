using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.Call;
using AgentCore.AspNetCore.DependencyInjection;
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
/// The Responses path: an OpenAI-compatible <c>POST /v1/responses</c> over the turn loop,
/// with the call continuing across requests through the session store.
/// </summary>
public static class ResponsesEndpointRouteBuilderExtensions
{
    /// <summary>The route this endpoint answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/responses";

    /// <summary>The answer header that reports the stage the machine holds.</summary>
    public const string StageHeaderName = "X-AgentCore-Stage";

    /// <summary>
    /// The request header that names the zone the person is in, as an IANA id such as
    /// <c>America/Chicago</c>. The browser knows it and the server does not. Read on every turn,
    /// so a person who travels moves the clock with them; a bad or missing value changes nothing.
    /// </summary>
    public const string TimeZoneHeaderName = "X-AgentCore-Time-Zone";

    /// <summary>The OpenAI error type every refused request reports.</summary>
    private const string InvalidRequestError = "invalid_request_error";

    /// <summary>Maps the endpoint for one entry on <see cref="DefaultPattern"/>.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="entry">The entry key this route answers on.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints, string entry)
        => endpoints.MapResponses(DefaultPattern, entry);

    /// <summary>Maps the endpoint for one entry on one route.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <param name="entry">The entry key this route answers on.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapResponses(
        this IEndpointRouteBuilder endpoints, string pattern, string entry)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentException.ThrowIfNullOrEmpty(entry);

        return endpoints.MapPost(pattern, (HttpContext http) => HandleAsync(http, entry))
            .WithMetadata(new AgentCoreEntryMetadata(entry, "Responses"));
    }

    /// <summary>Runs one turn of one call, and files the session under the ids the answer carries.</summary>
    private static async Task HandleAsync(HttpContext http, string entry)
    {
        var cancellationToken = http.RequestAborted;

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

        AgentCoreAgent agent;
        try
        {
            agent = http.RequestServices.GetRequiredService<AgentCoreBoot>().Entries.ForAgent(entry);
        }
        catch (InvalidOperationException failure)
        {
            await ResponsesRequestReader.WriteErrorAsync(
                http,
                StatusCodes.Status503ServiceUnavailable,
                failure.Message,
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
                await WriteTurnAsync(http, agent, sessions, session, call, input, origin, responseId, conversationId, cancellationToken)
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

    /// <summary>Runs one turn and answers the whole reply, filed under its ids.</summary>
    private static async Task WriteTurnAsync(
        HttpContext http,
        AgentCoreAgent agent,
        AgentCoreAgentSessionStore sessions,
        AgentSession session,
        CallSession call,
        ChatMessage input,
        CallTurnOrigin? origin,
        string responseId,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        var turn = await call
            .RunTurnMessageAtOriginAsync(input, origin, cancellationToken)
            .ConfigureAwait(false);

        await SaveAsync(sessions, agent, session, responseId, conversationId, cancellationToken)
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

        foreach (var (name, value) in ResponsesAgentCore.TurnMetadata(call, turn))
        {
            metadata[name] = value;
        }

        node["metadata"] = metadata;

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers[StageHeaderName] = turn.StageAfter;
        
        await http.Response
            .WriteAsJsonAsync(node, cancellationToken)
            .ConfigureAwait(false);
    }
    
    /// <summary>Files the session the turn just advanced, under every id that names it now.</summary>
    /// <remarks>
    /// A conversation id is stable across turns and a response id changes each one,
    /// so a turn that carried a conversation is filed under both: the next turn may
    /// continue by either pointer. A chain turn without a conversation files under
    /// its new response id alone.
    /// </remarks>
    internal static async Task SaveAsync(
        AgentCoreAgentSessionStore sessions,
        AIAgent agent,
        AgentSession session,
        string responseId,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId is not null)
        {
            await sessions.SaveSessionAsync(agent, conversationId, session, cancellationToken).ConfigureAwait(false);
        }

        await sessions.SaveSessionAsync(agent, responseId, session, cancellationToken).ConfigureAwait(false);
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools;
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
/// <remarks>
/// <para>
/// This is the app-owned counterpart to the framework's <c>MapOpenAIResponses</c>,
/// which owns conversation-items storage and runs every turn sessionless. Here the
/// protocol conversion alone comes from the framework
/// (<c>OpenAIResponses.ToAgentRunRequest</c>, <c>GetSessionStoreId</c>,
/// <c>WriteResponse</c>, <c>WriteResponseStreamAsync</c>); the route, the session
/// save and load, and the storage stay here, so a second turn resumes the same
/// call — its stage, its slots, and its words — rather than starting a new one.
/// </para>
/// <para>
/// The turn itself runs on the <see cref="CallSession"/> directly rather than through
/// the <c>AIAgent</c> seam, for the two things that seam cannot carry: where the turn
/// hangs (<see cref="CallTurnOrigin"/>) and an approval answer. The session save and
/// load stay on the agent seam, and the reply is rendered through it, so the stored
/// envelope keeps the one shape the agent reads back.
/// </para>
/// <para>
/// Turn facts travel in the response <c>metadata</c>, the spec's own extension point:
/// <c>call_id</c>, <c>turn_index</c>, <c>stage_before</c>, <c>stage_after</c>,
/// <c>is_terminal</c>, and, when set, <c>message_id</c>, <c>extraction_failure</c>,
/// and <c>approvals</c> (a JSON array of the pending requests). A typed OpenAI client
/// reads these; no bespoke top-level member is added. The stream carries text only —
/// its frames are the framework's fixed shapes — with the stage on a header; renders,
/// sources, and tool-call halves stay a chat-completions-stream capability, and the
/// words stay in store 1 either way.
/// </para>
/// </remarks>
public static class ResponsesEndpointRouteBuilderExtensions
{
    /// <summary>The route this endpoint answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/responses";

    /// <summary>The answer header that reports the stage the machine holds.</summary>
    public const string StageHeaderName = "X-AgentCore-Stage";

    /// <summary>Maps the endpoint on <see cref="DefaultPattern"/>.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints)
        => endpoints.MapResponses(DefaultPattern);

    /// <summary>Maps the endpoint on one route.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        return endpoints.MapPost(pattern, HandleAsync);
    }

    /// <summary>Runs one turn of one call, and files the session under the ids the answer carries.</summary>
    private static async Task HandleAsync(HttpContext http)
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
            await WriteErrorAsync(
                http,
                StatusCodes.Status400BadRequest,
                "the request body is not well-formed JSON: " + exception.Message,
                "invalid_request_error",
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
                await WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request is not a Responses request: " + exception.Message,
                    "invalid_request_error",
                    "invalid_body",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            runRequest = null;
            messages = [];
            conversationId = ReadConversationId(body);
        }

        var agent = http.RequestServices.GetRequiredService<AgentCoreAgent>();
        var sessions = http.RequestServices.GetRequiredService<AgentCoreAgentSessionStore>();

        // The continuation this turn hangs off, or null for the first turn of a call.
        // A conversation id names the call itself; a response id names one past turn of it.
        // The helper prefers the response chain over the conversation, exactly as the
        // protocol treats them as mutually exclusive; the refused-parse fallback reads
        // the same precedence off the body.
        var key = runRequest is not null
            ? OpenAIResponses.GetSessionStoreId(runRequest)
            : ReadContinuationId(body);

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
                await WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "an approval answer names the call it resumes. Send it with the conversation "
                    + "or previous response id the asking turn answered with.",
                    "invalid_request_error",
                    "missing_session",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            conversationId = NewConversationId();
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
            session = await sessions.GetSessionAsync(agent, key, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Either an unknown response id — a turn no answer ever carried — or an unknown
            // conversation beside an approval-only body the parse refused. A conversation with words
            // reaches the namesNewCall branch; an approval never starts a call.
            await WriteErrorAsync(
                http,
                StatusCodes.Status404NotFound,
                $"no call opens under '{key}'. Send the request with neither a conversation nor a "
                + "previous response id to start one.",
                "invalid_request_error",
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
            if (LastUserText(messages) is { Length: > 0 })
            {
                await WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request carries both a user message and an approval answer. One turn carries "
                    + "either words or an answer, never both.",
                    "invalid_request_error",
                    "mixed_turn",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            // Async: a resumed call has run no turn in this process, so its ledger session —
            // and the queue the suspending turn filed — is still closed. This opens it first.
            if (await call.TryCreateApprovalAnswerAsync(approval.RequestId, approval.Approved, cancellationToken).ConfigureAwait(false) is not { } answer)
            {
                await WriteErrorAsync(
                    http,
                    StatusCodes.Status409Conflict,
                    $"the call queues no approval under id '{approval.RequestId}'. It was "
                    + "answered already, belongs to another call, or was never asked.",
                    "invalid_request_error",
                    "no_pending_approval",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            input = answer;
        }
        else if (LastUserMessage(messages) is not { } user)
        {
            await WriteErrorAsync(
                http,
                StatusCodes.Status400BadRequest,
                "the request carries no user message with text, so there is no turn to run.",
                "invalid_request_error",
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

        // Only the stream has a chunk to carry a drawing, for the same reason the chat
        // endpoint binds its screen per branch: the session outlives a request and the
        // branch can differ per turn.
        call.SetHasScreen(streaming);

        try
        {
            if (streaming)
            {
                // The dialect is opt-in by the member only our clients send: an OpenAI SDK
                // never carries agentcore, so its stream stays the framework's pure shapes.
                await StreamTurnAsync(http, agent, sessions, session, call, input, origin, responseId, conversationId, agentcore is not null, cancellationToken)
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
            await WriteErrorAsync(
                http,
                StatusCodes.Status409Conflict,
                exception.Message,
                "invalid_request_error",
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
    /// <summary>Runs one turn and writes one Responses event for each update, filed under its ids.</summary>
    /// <remarks>
    /// The session is filed after the enumeration ends, because the turn commits —
    /// and the session only holds the turn — once the last update has left it.
    /// The framework's frames carry text alone; when the request spoke the dialect,
    /// each update's browser parts ride beside them as the same member shapes the chat
    /// endpoint writes, so drawings, citations, tool halves, and approval asks stream.
    /// </remarks>
    private static async Task StreamTurnAsync(
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
        http.Response.Headers[StageHeaderName] = call.Stage;

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

        await SaveAsync(sessions, agent, session, responseId, conversationId, cancellationToken)
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
            }

            yield return new AgentResponseUpdate(update) { AgentId = agent.Id };
        }
    }

    /// <summary>Writes one dialect event: the member shapes the chat endpoint writes, bare.</summary>
    private static async Task WritePartLineAsync(
        HttpContext http, TurnStreamPart part, CancellationToken cancellationToken)
    {
        JsonObject line = part switch
        {
            TurnStreamRender render
                => new JsonObject { ["agentcore_data"] = JsonSerializer.SerializeToNode(render.Payload, ChatCompletionJson.Options) },
            TurnStreamSource source
                => new JsonObject { ["agentcore_source"] = JsonSerializer.SerializeToNode(source.Payload, ChatCompletionJson.Options) },
            TurnStreamTool tool
                => new JsonObject { ["agentcore_tool"] = JsonSerializer.SerializeToNode(tool.Payload, ChatCompletionJson.Options) },
            TurnStreamApproval approval
                => new JsonObject { ["agentcore_approval"] = JsonSerializer.SerializeToNode(approval.Payload, ChatCompletionJson.Options) },
            _ => throw new InvalidOperationException($"Unknown stream part: {part.GetType()}."),
        };

        await http.Response.WriteAsync("data: ", cancellationToken).ConfigureAwait(false);
        await http.Response.WriteAsync(line.ToJsonString(), cancellationToken).ConfigureAwait(false);
        await http.Response.WriteAsync("\n\n", cancellationToken).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Files the session the turn just advanced, under every id that names it now.</summary>
    /// <remarks>
    /// A conversation id is stable across turns and a response id changes each one,
    /// so a turn that carried a conversation is filed under both: the next turn may
    /// continue by either pointer. A chain turn without a conversation files under
    /// its new response id alone.
    /// </remarks>
    private static async Task SaveAsync(
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

    /// <summary>Reads the last user message that carries words, the session owning the rest.</summary>
    private static ChatMessage? LastUserMessage(IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage? picked = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User && message.Text is { Length: > 0 })
            {
                picked = message;
            }
        }

        return picked;
    }

    /// <summary>Reads whether any user message of the run carries words.</summary>
    private static string? LastUserText(IReadOnlyList<ChatMessage> messages)
        => LastUserMessage(messages)?.Text;

    /// <summary>Reads the conversation id off a body the protocol parse refused.</summary>
    private static string? ReadConversationId(JsonElement body)
    {
        if (body.TryGetProperty("conversation", out var conversation))
        {
            if (conversation.ValueKind == JsonValueKind.String)
            {
                return conversation.GetString();
            }

            if (conversation.ValueKind == JsonValueKind.Object
                && conversation.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }

        return null;
    }

    /// <summary>Reads the continuation off a body the protocol parse refused.</summary>
    private static string? ReadContinuationId(JsonElement body)
    {
        if (body.TryGetProperty("previous_response_id", out var response)
            && response.ValueKind == JsonValueKind.String
            && response.GetString() is { Length: > 0 } responseId)
        {
            return responseId;
        }

        return ReadConversationId(body);
    }

    /// <summary>Answers one failure in the shape an OpenAI client reads.</summary>
    private static async Task WriteErrorAsync(
        HttpContext http,
        int status,
        string message,
        string type,
        string code,
        CancellationToken cancellationToken)
    {
        http.Response.StatusCode = status;

        await http.Response.WriteAsJsonAsync(
            new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = message,
                    ["type"] = type,
                    ["code"] = code,
                    ["param"] = null,
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the id of one conversation.</summary>
    /// <returns>The id, in the shape an OpenAI client already reads.</returns>
    private static string NewConversationId()
        => string.Create(CultureInfo.InvariantCulture, $"conv_{Guid.NewGuid():N}");
}

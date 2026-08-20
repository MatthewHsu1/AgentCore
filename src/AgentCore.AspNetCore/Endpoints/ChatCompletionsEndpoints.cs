using System.Globalization;
using System.Text.Json;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Sessions;
using AgentCore.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The text path: an OpenAI-compatible <c>POST /v1/chat/completions</c> over the turn loop.
/// </summary>
/// <remarks>
/// <para>
/// One request runs one turn. The endpoint reads the last <c>user</c> message of the request, hands
/// it to <see cref="CallSession.RunTurnAsync"/>, and answers the reply. With <c>stream: true</c> it
/// runs <see cref="CallSession.RunTurnStreamingAsync"/> instead and writes one server-sent event for
/// each update, so the first word leaves the host as soon as the model produced it.
/// </para>
/// <para>
/// A request names its session in the <see cref="SessionHeaderName"/> header. The OpenAI request
/// shape carries no session field, so adding one would break a strict client; a header rides beside
/// the shape and every client can set one. A request that sets no header starts a new call, and the
/// answer carries the id in the same header and in the <c>agentcore.session</c> field of the body. A
/// request that names an id the store does not hold is answered <c>404</c> and starts nothing,
/// because a silently new call would lose the stage the caller was in.
/// </para>
/// <para>
/// The session lives in <see cref="ICallSessionStore"/> between two requests. The default store is
/// <see cref="InMemoryCallSessionStore"/>, which does not survive a restart and does not span
/// instances.
/// </para>
/// </remarks>
public static class ChatCompletionsEndpointRouteBuilderExtensions
{
    /// <summary>The route this endpoint answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/chat/completions";

    /// <summary>The header that names the session, on the request and on the answer.</summary>
    public const string SessionHeaderName = "X-AgentCore-Session";

    /// <summary>The answer header that reports the stage the machine holds after the turn.</summary>
    /// <remarks>
    /// The streaming path writes its headers before the turn ends, so it reports the stage the turn
    /// spoke in. Read <c>agentcore.stage_after</c> of the last chunk for the stage after the turn.
    /// </remarks>
    public const string StageHeaderName = "X-AgentCore-Stage";

    private const string EventPrefix = "data: ";
    private const string EventSuffix = "\n\n";
    private const string DoneEvent = "data: [DONE]\n\n";

    /// <summary>Maps the endpoint on <see cref="DefaultPattern"/>.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapChatCompletions(this IEndpointRouteBuilder endpoints)
        => endpoints.MapChatCompletions(DefaultPattern);

    /// <summary>Maps the endpoint on one route.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapChatCompletions(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        return endpoints.MapPost(pattern, HandleAsync);
    }

    /// <summary>Runs one turn of one call.</summary>
    /// <param name="http">The request.</param>
    /// <returns>A task that completes when the answer is written.</returns>
    private static async Task HandleAsync(HttpContext http)
    {
        var cancellationToken = http.RequestAborted;

        ChatCompletionRequest? request;
        try
        {
            request = await JsonSerializer
                .DeserializeAsync<ChatCompletionRequest>(http.Request.Body, ChatCompletionJson.Options, cancellationToken)
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

        if (LastUserText(request) is not { Length: > 0 } input)
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

        var store = http.RequestServices.GetRequiredService<ICallSessionStore>();
        var named = http.Request.Headers[SessionHeaderName].ToString();

        CallSession session;
        if (named is { Length: > 0 })
        {
            if (await store.TryGetAsync(named, cancellationToken).ConfigureAwait(false) is not { } found)
            {
                await WriteErrorAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    $"no call named '{named}' is open on this host. Send the request with no "
                    + SessionHeaderName + " header to start one.",
                    "invalid_request_error",
                    "session_not_found",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            session = found;
        }
        else
        {
            session = http.RequestServices.GetRequiredService<ICallSessionFactory>().Create();
            await store.AddAsync(session, cancellationToken).ConfigureAwait(false);
        }

        var model = request?.Model is { Length: > 0 } asked ? asked : session.Compiled.Name;

        try
        {
            if (request?.Stream == true)
            {
                await StreamTurnAsync(http, session, input, model, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteTurnAsync(http, session, input, model, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Runs one turn and answers the whole reply.</summary>
    /// <param name="http">The request.</param>
    /// <param name="session">The session of the call.</param>
    /// <param name="input">What the caller said.</param>
    /// <param name="model">The model name the answer reports.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>A task that completes when the answer is written.</returns>
    private static async Task WriteTurnAsync(
        HttpContext http,
        CallSession session,
        string input,
        string model,
        CancellationToken cancellationToken)
    {
        var turn = await session.RunTurnAsync(input, cancellationToken).ConfigureAwait(false);

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers[SessionHeaderName] = session.CallId;
        http.Response.Headers[StageHeaderName] = turn.StageAfter;

        ChatCompletionResponse body = new()
        {
            Id = NewCompletionId(),
            Object = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices =
            [
                new ChatCompletionChoice
                {
                    Index = 0,
                    Message = new ChatCompletionMessage { Role = "assistant", Content = turn.ReplyText },
                    FinishReason = "stop",
                },
            ],
            AgentCore = Describe(session, turn),
        };

        await http.Response.WriteAsJsonAsync(body, ChatCompletionJson.Options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one turn and writes one server-sent event for each update.</summary>
    /// <param name="http">The request.</param>
    /// <param name="session">The session of the call.</param>
    /// <param name="input">What the caller said.</param>
    /// <param name="model">The model name the answer reports.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>A task that completes when the stream closes.</returns>
    private static async Task StreamTurnAsync(
        HttpContext http,
        CallSession session,
        string input,
        string model,
        CancellationToken cancellationToken)
    {
        var id = NewCompletionId();
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers[SessionHeaderName] = session.CallId;

        // The headers leave before the turn ends, so this one names the stage the turn speaks in.
        http.Response.Headers[StageHeaderName] = session.Stage;

        // The first chunk carries the role and no text, which is what an OpenAI client expects.
        await WriteEventAsync(
            http,
            Chunk(id, created, model, new ChatCompletionMessage { Role = "assistant" }, finishReason: null, turn: null),
            cancellationToken).ConfigureAwait(false);

        await foreach (var update in session.RunTurnStreamingAsync(input, cancellationToken).ConfigureAwait(false))
        {
            if (update.Text is not { Length: > 0 } text)
            {
                // A tool call and a tool result arrive as updates too, and neither is speech.
                continue;
            }

            await WriteEventAsync(
                http,
                Chunk(id, created, model, new ChatCompletionMessage { Content = text }, finishReason: null, turn: null),
                cancellationToken).ConfigureAwait(false);
        }

        // The turn is finished once the enumeration is, so the last chunk is the first place that
        // knows the stage the machine moved to.
        await WriteEventAsync(
            http,
            Chunk(id, created, model, new ChatCompletionMessage(), "stop", session.LastTurn is { } last ? Describe(session, last) : null),
            cancellationToken).ConfigureAwait(false);

        await http.Response.WriteAsync(DoneEvent, cancellationToken).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds one chunk of a stream.</summary>
    /// <param name="id">The id every chunk of one reply shares.</param>
    /// <param name="created">When the reply started.</param>
    /// <param name="model">The model name the answer reports.</param>
    /// <param name="delta">The piece this chunk carries.</param>
    /// <param name="finishReason">Why the reply stopped, or <see langword="null"/>.</param>
    /// <param name="turn">What the turn did, on the last chunk only.</param>
    /// <returns>The chunk.</returns>
    private static ChatCompletionResponse Chunk(
        string id,
        long created,
        string model,
        ChatCompletionMessage delta,
        string? finishReason,
        AgentCoreTurnInfo? turn)
        => new()
        {
            Id = id,
            Object = "chat.completion.chunk",
            Created = created,
            Model = model,
            Choices = [new ChatCompletionChoice { Index = 0, Delta = delta, FinishReason = finishReason }],
            AgentCore = turn,
        };

    /// <summary>Writes one server-sent event and flushes it.</summary>
    /// <param name="http">The request.</param>
    /// <param name="body">What the event carries.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the event reaches the socket.</returns>
    private static async Task WriteEventAsync(
        HttpContext http,
        ChatCompletionResponse body,
        CancellationToken cancellationToken)
    {
        await http.Response.WriteAsync(EventPrefix, cancellationToken).ConfigureAwait(false);
        await http.Response
            .WriteAsync(JsonSerializer.Serialize(body, ChatCompletionJson.Options), cancellationToken)
            .ConfigureAwait(false);
        await http.Response.WriteAsync(EventSuffix, cancellationToken).ConfigureAwait(false);

        // Without this the reply arrives in one piece, which defeats the whole streaming path.
        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers one failure in the shape an OpenAI client reads.</summary>
    /// <param name="http">The request.</param>
    /// <param name="status">The status code.</param>
    /// <param name="message">What is wrong, in one sentence.</param>
    /// <param name="type">The family of the failure.</param>
    /// <param name="code">The machine-readable reason.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the answer is written.</returns>
    private static async Task WriteErrorAsync(
        HttpContext http,
        int status,
        string message,
        string type,
        string code,
        CancellationToken cancellationToken)
    {
        http.Response.StatusCode = status;
        ChatCompletionError body = new()
        {
            Error = new ChatCompletionErrorBody { Message = message, Type = type, Code = code },
        };

        await http.Response.WriteAsJsonAsync(body, ChatCompletionJson.Options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the last thing the caller said.</summary>
    /// <param name="request">The request, or <see langword="null"/> when the body was empty.</param>
    /// <returns>The text, or <see langword="null"/> when the request carries none.</returns>
    /// <remarks>
    /// The session owns the transcript, so an earlier message of the request is already in the call.
    /// The turn runs on the newest one, and the rest is what the client kept for its own display.
    /// </remarks>
    private static string? LastUserText(ChatCompletionRequest? request)
    {
        if (request?.Messages is not { Count: > 0 } messages)
        {
            return null;
        }

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (string.Equals(message.Role, "user", StringComparison.Ordinal) && message.Content is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>Describes what one finished turn did.</summary>
    /// <param name="session">The session of the call.</param>
    /// <param name="turn">The finished turn.</param>
    /// <returns>The extension object the answer carries.</returns>
    private static AgentCoreTurnInfo Describe(CallSession session, TurnResult turn)
        => new()
        {
            Session = session.CallId,
            TurnIndex = turn.TurnIndex,
            StageBefore = turn.StageBefore,
            StageAfter = turn.StageAfter,
            IsTerminal = turn.IsTerminal,
            ExtractionFailure = turn.ExtractionFailure,
        };

    /// <summary>Builds the id of one reply.</summary>
    /// <returns>The id, in the shape an OpenAI client already reads.</returns>
    private static string NewCompletionId()
        => string.Create(CultureInfo.InvariantCulture, $"chatcmpl-{Guid.NewGuid():N}");
}

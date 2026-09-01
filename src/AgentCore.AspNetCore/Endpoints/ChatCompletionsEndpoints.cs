using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The text path: an OpenAI-compatible <c>POST /v1/chat/completions</c> over the turn loop.
/// </summary>
public static class ChatCompletionsEndpointRouteBuilderExtensions
{
    /// <summary>The route this endpoint answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/chat/completions";

    /// <summary>The header that names the session, on the request and on the answer.</summary>
    public const string SessionHeaderName = "X-AgentCore-Session";

    /// <summary>The answer header that reports the stage the machine holds after the turn.</summary>

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

        var sessions = http.RequestServices.GetRequiredService<ICallSessions>();
        var named = http.Request.Headers[SessionHeaderName].ToString();

        CallSession session;
        if (named is { Length: > 0 })
        {
            if (await sessions.TryGetAsync(named, cancellationToken).ConfigureAwait(false) is not { } found)
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
            session = await sessions.OpenAsync(null, cancellationToken).ConfigureAwait(false);
        }

        var streaming = request?.Stream == true;

        // Only the stream has a chunk to carry a drawing, so only the stream gets a screen. Binding
        // one on the whole-reply branch would let the tool report a picture the caller never sees;
        // with none bound it answers that it cannot draw. Set per request, not once: the session
        // outlives a request and the branch can differ per turn.
        session.SetHasScreen(streaming);

        var model = request?.Model is { Length: > 0 } asked ? asked : session.Compiled.Name;

        // Absent means a client that does not track its messages by name, so its words simply go on
        // the end. Present means the client answers for where the turn belongs, and a parent further
        // back than the last thing said is what makes this an edit.
        var origin = request?.AgentCore is { } placed
            ? new CallTurnOrigin(placed.MessageId, placed.ParentId) { NamesParent = placed.NamesParent }
            : null;

        try
        {
            if (streaming)
            {
                await StreamTurnAsync(http, session, input, model, origin, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteTurnAsync(http, session, input, model, origin, cancellationToken).ConfigureAwait(false);
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
    /// <param name="origin">Where the caller says these words hang, or null when it does not say.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>A task that completes when the answer is written.</returns>
    private static async Task WriteTurnAsync(
        HttpContext http,
        CallSession session,
        string input,
        string model,
        CallTurnOrigin? origin,
        CancellationToken cancellationToken)
    {
        var turn = await session.RunTurnAtOriginAsync(input, origin, cancellationToken).ConfigureAwait(false);

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
    /// <param name="origin">Where the caller says these words hang, or null when it does not say.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>A task that completes when the stream closes.</returns>
    private static async Task StreamTurnAsync(
        HttpContext http,
        CallSession session,
        string input,
        string model,
        CallTurnOrigin? origin,
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

        // One turn's worth of ids: the pairing dies with the stream.
        ToolCallNames toolNames = new();

        await foreach (var update in session.RunTurnStreamingAtOriginAsync(input, origin, cancellationToken).ConfigureAwait(false))
        {
            foreach (var drawn in update.Contents.OfType<RenderContent>())
            {
                await WriteEventAsync(
                    http,
                    Chunk(id, created, model, new ChatCompletionMessage(), finishReason: null, turn: null)
                        with
                    { AgentCoreData = new RenderedPayload { Name = drawn.Name, Data = drawn.Data } },
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var cited in update.Contents.OfType<SourceContent>())
            {
                await WriteEventAsync(
                    http,
                    Chunk(id, created, model, new ChatCompletionMessage(), finishReason: null, turn: null)
                        with
                    { AgentCoreSource = SourcePayloadOf(cited) },
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var tool in ToolPayloadsOf(update, toolNames))
            {
                await WriteEventAsync(
                    http,
                    Chunk(id, created, model, new ChatCompletionMessage(), finishReason: null, turn: null)
                        with
                    { AgentCoreTool = tool },
                    cancellationToken).ConfigureAwait(false);
            }

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

    /// <summary>Reads the tool facts one update carries, in the order they appear on it.</summary>
    /// <param name="update">One update of the stream.</param>
    /// <param name="toolNames">The pairing of call id to tool name for this turn, written and read here.</param>
    /// <returns>One payload for each half of a tool call the update carries, possibly none.</returns>
    private static IEnumerable<ToolPayload> ToolPayloadsOf(
        ChatResponseUpdate update,
        ToolCallNames toolNames)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    toolNames.Called(call);
                    yield return new ToolPayload
                    {
                        CallId = call.CallId,
                        Name = call.Name,
                        Phase = "call",
                        Arguments = ArgumentsOf(call),
                    };
                    break;

                case FunctionResultContent result:
                    // The answer has no declared shape, so it goes through the one reader that knows
                    // every shape it arrives in. Reading it twice would let the two disagree.
                    var answer = ToolResultJson.ToNode(result.Result);
                    yield return new ToolPayload
                    {
                        CallId = result.CallId,
                        // A result that arrives with no call before it should not be possible, and
                        // naming it after its id beats naming it nothing if it ever is.
                        Name = toolNames.Of(result) ?? result.CallId,
                        Phase = "result",
                        Result = ResultOf(result, answer),
                        Failed = result.Exception is not null || ToolErrorResult.IsError(answer),
                    };
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>Reads one cited source into what the browser receives.</summary>
    /// <param name="cited">The source the message carried.</param>
    /// <returns>The payload.</returns>
    private static SourcePayload SourcePayloadOf(SourceContent cited)
        => new()
        {
            CallId = cited.CallId,
            Id = cited.Source.SourceId,
            SourceType = cited.Source.Kind == SourceKind.Url ? "url" : "document",
            Title = cited.Source.Title,
            Locator = cited.Source.Locator,
            Url = cited.Source.Url,
            MediaType = cited.Source.MediaType,
            Origin = cited.Source.Origin,
        };

    /// <summary>Reads what the model passed to one tool, or <see langword="null"/> when it passed nothing.</summary>
    private static JsonObject? ArgumentsOf(FunctionCallContent call)
        => call.Arguments is { Count: > 0 } arguments ? ToolArgumentJson.ToJsonObject(arguments) : null;

    /// <summary>Reads what one tool answered, as the browser receives it.</summary>
    /// <param name="result">The result half of the call.</param>
    /// <param name="answer">The same result read as JSON, or <see langword="null"/> when it answered nothing.</param>
    private static JsonNode? ResultOf(FunctionResultContent result, JsonNode? answer)
        => result.Exception is { } failure
            ? JsonValue.Create(failure.GetType().Name + ": " + failure.Message)
            : answer;

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
            MessageId = session.LastReplyMessageId,
        };

    /// <summary>Builds the id of one reply.</summary>
    /// <returns>The id, in the shape an OpenAI client already reads.</returns>
    private static string NewCompletionId()
        => string.Create(CultureInfo.InvariantCulture, $"chatcmpl-{Guid.NewGuid():N}");
}

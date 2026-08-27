using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The wire shapes of <c>POST /v1/chat/completions</c>.
/// </summary>
internal static class ChatCompletionJson
{
    /// <summary>The one serializer setting both directions use.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One message of a request or of a reply.</summary>
internal sealed record ChatCompletionMessage
{
    /// <summary>Gets the role, such as <c>user</c> or <c>assistant</c>.</summary>
    public string? Role { get; init; }

    /// <summary>Gets the text. This endpoint reads and writes text, and no other content part.</summary>
    public string? Content { get; init; }
}

/// <summary>One request.</summary>
internal sealed record ChatCompletionRequest
{
    /// <summary>Gets the model the client asked for. The document decides what actually answers.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the conversation the client sent, oldest first.</summary>
    public IReadOnlyList<ChatCompletionMessage>? Messages { get; init; }

    /// <summary>Gets whether the client wants server-sent events.</summary>
    public bool? Stream { get; init; }
}

/// <summary>What one finished turn did, carried beside the OpenAI shape.</summary>
internal sealed record AgentCoreTurnInfo
{
    /// <summary>Gets the id that names this session on the next request.</summary>
    public required string Session { get; init; }

    /// <summary>Gets the zero-based index of the turn that just ran.</summary>
    public required int TurnIndex { get; init; }

    /// <summary>Gets the stage the turn spoke in.</summary>
    public required string StageBefore { get; init; }

    /// <summary>Gets the stage the machine holds after the turn.</summary>
    public required string StageAfter { get; init; }

    /// <summary>Gets whether the stage after the turn ends the call.</summary>
    public required bool IsTerminal { get; init; }

    /// <summary>Gets the reason the extractor produced nothing, or <see langword="null"/>.</summary>
    public string? ExtractionFailure { get; init; }
}

/// <summary>One choice of a reply. It carries <c>message</c> when whole and <c>delta</c> when streamed.</summary>
internal sealed record ChatCompletionChoice
{
    /// <summary>Gets the index of the choice. This endpoint answers one choice, so it is always zero.</summary>
    public required int Index { get; init; }

    /// <summary>Gets the whole message, on the non-streaming path.</summary>
    public ChatCompletionMessage? Message { get; init; }

    /// <summary>Gets one piece of the message, on the streaming path.</summary>
    public ChatCompletionMessage? Delta { get; init; }

    /// <summary>Gets why the reply stopped, or <see langword="null"/> while it continues.</summary>
    public string? FinishReason { get; init; }
}

/// <summary>One reply, whole or as one chunk of a stream.</summary>
internal sealed record ChatCompletionResponse
{
    /// <summary>Gets the id of this reply.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the kind of reply: <c>chat.completion</c> or <c>chat.completion.chunk</c>.</summary>
    [JsonPropertyName("object")]
    public required string Object { get; init; }

    /// <summary>Gets when the reply started, as a Unix time in seconds.</summary>
    public required long Created { get; init; }

    /// <summary>Gets the model name the reply reports.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the choices. This endpoint answers one.</summary>
    public required IReadOnlyList<ChatCompletionChoice> Choices { get; init; }

    /// <summary>Gets what the turn did, or <see langword="null"/> before the turn ends.</summary>
    [JsonPropertyName("agentcore")]
    public AgentCoreTurnInfo? AgentCore { get; init; }

    /// <summary>Gets what this chunk asks the browser to draw, or <see langword="null"/> when nothing.</summary>
    [JsonPropertyName("agentcore_data")]
    public RenderedPayload? AgentCoreData { get; init; }
}

/// <summary>One thing a chunk asks the browser to draw.</summary>
internal sealed record RenderedPayload
{
    /// <summary>Gets the renderer the browser looks up.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the payload that renderer reads.</summary>
    public required JsonElement Data { get; init; }
}

/// <summary>The body of one failure.</summary>
internal sealed record ChatCompletionErrorBody
{
    /// <summary>Gets what is wrong, in one sentence.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the family of the failure, such as <c>invalid_request_error</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Gets the machine-readable reason, such as <c>session_not_found</c>.</summary>
    public string? Code { get; init; }
}

/// <summary>One failure, in the shape an OpenAI client already reads.</summary>
internal sealed record ChatCompletionError
{
    /// <summary>Gets the failure.</summary>
    public required ChatCompletionErrorBody Error { get; init; }
}

using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Gets what the client says about where this message belongs, if it says anything.</summary>
    [JsonPropertyName("agentcore")]
    public AgentCoreRequestInfo? AgentCore { get; init; }
}

/// <summary>Where the client says its message belongs, carried beside the OpenAI shape.</summary>
internal sealed record AgentCoreRequestInfo
{
    /// <summary>Gets what the client calls the message it is sending.</summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }

    /// <summary>Gets what the client calls the message this one hangs off. Null starts the call afresh.</summary>
    [JsonPropertyName("parent_id")]
    public string? ParentId
    {
        get => _parentId;
        init
        {
            _parentId = value;
            NamesParent = true;
        }
    }

    /// <summary>Gets whether the body carried <c>parent_id</c> at all.</summary>
    [JsonIgnore]
    public bool NamesParent { get; private init; }

    private readonly string? _parentId;
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

    /// <summary>Gets what the host calls the reply it just wrote, for a later edit to hang off.</summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }
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

    /// <summary>Gets the tool fact this chunk carries, or <see langword="null"/> when none.</summary>
    [JsonPropertyName("agentcore_tool")]
    public ToolPayload? AgentCoreTool { get; init; }

    /// <summary>Gets the source this chunk carries, or <see langword="null"/> when none.</summary>
    [JsonPropertyName("agentcore_source")]
    public SourcePayload? AgentCoreSource { get; init; }
}

/// <summary>One half of one tool call, as the browser reads it.</summary>
internal sealed record ToolPayload
{
    /// <summary>Gets the id both halves of one call share.</summary>
    public required string CallId { get; init; }

    /// <summary>Gets the name of the tool.</summary>
    public required string Name { get; init; }

    /// <summary>Gets which half this is: <c>call</c> or <c>result</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>Gets what the model passed, on the <c>call</c> half only.</summary>
    public JsonNode? Arguments { get; init; }

    /// <summary>Gets what the tool answered, on the <c>result</c> half only.</summary>
    public JsonNode? Result { get; init; }

    /// <summary>Gets whether the tool failed, on the <c>result</c> half only.</summary>
    public bool? Failed { get; init; }
}

/// <summary>Where one answer came from, as the browser reads it.</summary>
internal sealed record SourcePayload
{
    /// <summary>Gets the tool call this source was cited under.</summary>
    public required string CallId { get; init; }

    /// <summary>Gets the id of this source, unique within one turn.</summary>
    public required string Id { get; init; }

    /// <summary>Gets which shape it takes: <c>document</c> or <c>url</c>.</summary>
    public required string SourceType { get; init; }

    /// <summary>Gets what the source is called.</summary>
    public required string Title { get; init; }

    /// <summary>Gets where inside the source it sits, such as <c>p.27</c>. Empty when it has none.</summary>
    public required string Locator { get; init; }

    /// <summary>Gets the link to open, or <see langword="null"/> when there is nothing to open.</summary>
    public string? Url { get; init; }

    /// <summary>Gets the media type of the source.</summary>
    public required string MediaType { get; init; }

    /// <summary>Gets what produced this source, such as <c>knowledge</c>.</summary>
    public required string Origin { get; init; }
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

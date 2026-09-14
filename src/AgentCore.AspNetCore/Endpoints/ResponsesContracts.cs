using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentCore.Application.Runtime;
using AgentCore.Domain;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The wire shapes of <c>POST /v1/responses</c> that are ours, beside the OpenAI shape.
/// </summary>
internal static class ResponsesAgentCore
{
    /// <summary>Reads the <c>agentcore</c> member of one request body, if it names one.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>What the client said about this turn, or <see langword="null"/> for a plain turn.</returns>
    internal static ResponsesRequestInfo? ReadRequest(JsonElement body)
    {
        if (!body.TryGetProperty("agentcore", out var agentcore)
            || agentcore.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? messageId = null;
        if (agentcore.TryGetProperty("message_id", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            messageId = message.GetString();
        }

        string? parentId = null;
        var namesParent = agentcore.TryGetProperty("parent_id", out var parent);
        if (namesParent && parent.ValueKind == JsonValueKind.String)
        {
            parentId = parent.GetString();
        }

        ResponsesApprovalAnswer? approval = null;
        if (agentcore.TryGetProperty("approval", out var answer)
            && answer.ValueKind == JsonValueKind.Object
            && answer.TryGetProperty("request_id", out var requestId)
            && requestId.ValueKind == JsonValueKind.String
            && requestId.GetString() is { Length: > 0 } id
            && answer.TryGetProperty("approved", out var approved)
            && (approved.ValueKind == JsonValueKind.True || approved.ValueKind == JsonValueKind.False))
        {
            approval = new ResponsesApprovalAnswer(id, approved.ValueKind == JsonValueKind.True);
        }

        if (messageId is null && !namesParent && approval is null)
        {
            return null;
        }

        return new ResponsesRequestInfo(messageId, parentId, namesParent, approval);
    }

    /// <summary>Builds the <c>metadata</c> one finished turn files onto its answer.</summary>
    /// <param name="call">The call the turn ran on.</param>
    /// <param name="turn">The finished turn.</param>
    /// <returns>String pairs: the protocol's metadata holds strings only, and at most 16.</returns>
    internal static IDictionary<string, string> TurnMetadata(CallSession call, TurnResult turn)
    {
        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["call_id"] = call.CallId,
            ["turn_index"] = turn.TurnIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["stage_before"] = turn.StageBefore,
            ["stage_after"] = turn.StageAfter,
            ["is_terminal"] = turn.IsTerminal ? "true" : "false",
        };

        if (call.LastReplyMessageId is { } messageId)
        {
            metadata["message_id"] = messageId;
        }

        if (turn.ExtractionFailure is { } failure)
        {
            metadata["extraction_failure"] = failure;
        }

        if (turn.Approvals.Count > 0)
        {
            metadata["approvals"] = ApprovalsJson(turn);
        }

        return metadata;
    }

    /// <summary>Answers where one turn hangs, in the shape the turn loop reads.</summary>
    /// <param name="info">What the client said, or <see langword="null"/> for a plain turn.</param>
    /// <returns>The origin, or <see langword="null"/> when the turn appends.</returns>
    internal static CallTurnOrigin? OriginOf(ResponsesRequestInfo? info)
        => info is null ? null : new CallTurnOrigin(info.MessageId, info.ParentId) { NamesParent = info.NamesParent };

    private static string ApprovalsJson(TurnResult turn)
    {
        System.Text.Json.Nodes.JsonArray approvals = [];
        foreach (var approval in turn.Approvals)
        {
            approvals.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["request_id"] = approval.RequestId,
                ["tool"] = approval.ToolName,
                ["arguments"] = System.Text.Json.Nodes.JsonNode.Parse(approval.Arguments.GetRawText()),
            });
        }

        return approvals.ToJsonString();
    }
}

/// <summary>Where the client says its turn belongs and what it answers, beside the OpenAI shape.</summary>
/// <param name="MessageId">What the client calls the message it is sending.</param>
/// <param name="ParentId">What the client calls the message this one hangs off. Null starts the call afresh.</param>
/// <param name="NamesParent">Whether the body carried <c>parent_id</c> at all.</param>
/// <param name="Approval">The approval answer this turn carries, or <see langword="null"/> when it holds words.</param>
internal sealed record ResponsesRequestInfo(
    string? MessageId,
    string? ParentId,
    bool NamesParent,
    ResponsesApprovalAnswer? Approval);

/// <summary>One approval answer: which request the caller answers, and whether the tool may run.</summary>
/// <param name="RequestId">The id of the pending request this answers.</param>
/// <param name="Approved">Whether the tool may run.</param>
internal sealed record ResponsesApprovalAnswer(string RequestId, bool Approved);

/// <summary>
/// The one serializer setting the dialect uses.
/// </summary>
internal static class ResponsesJson
{
    /// <summary>The one serializer setting both directions use.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
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

/// <summary>One tool call waiting on the caller, as the browser reads it.</summary>
internal sealed record ApprovalPayload
{
    /// <summary>Gets the id the approval answer carries back.</summary>
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    /// <summary>Gets the name of the tool the model asked to call.</summary>
    public required string Tool { get; init; }

    /// <summary>Gets what the model passed, or <see langword="null"/> when it passed nothing.</summary>
    public JsonNode? Arguments { get; init; }
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

/// <summary>One thing a stream asks the browser to draw.</summary>
internal sealed record RenderedPayload
{
    /// <summary>Gets the renderer the browser looks up.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the payload that renderer reads.</summary>
    public required JsonElement Data { get; init; }
}

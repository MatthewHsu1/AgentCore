using System.Text.Json;
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

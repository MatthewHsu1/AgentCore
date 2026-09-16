using System.Text.Json;
using AgentCore.Domain;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// The framework's pending approval queue, read off a session's state bag. The queue is a JSON
/// array of <c>{toolCall: {name, arguments, callId}, requiresConfirmation, requestId}</c> under
/// <see cref="PendingStateKey"/> — a MAF-internal shape pinned by probe
/// <c>docs/probes/harness-agentic/approval-roundtrip/</c>, not a contract. Every read here is
/// lenient: an entry that lost its shape is skipped, never thrown, because the turn must end
/// however the queue looks.
/// </summary>
internal static class PendingApprovalQueue
{
    /// <summary>What the function-invocation layer names its queue in the state bag.</summary>
    internal const string PendingStateKey = "_pendingApprovalRequests";

    /// <summary>What the approval layer names its standing and auto rules in the state bag.</summary>
    internal const string StandingStateKey = "toolApprovalState";

    /// <summary>Reads the queue out of a serialized state bag.</summary>
    /// <param name="bag">What <c>AgentSessionStateBag.Serialize</c> produced.</param>
    /// <returns>The pending approvals, oldest first; empty when the bag carries no queue.</returns>
    internal static IReadOnlyList<PendingApproval> Read(JsonElement bag)
    {
        if (!bag.TryGetProperty(PendingStateKey, out var queue)
            || queue.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PendingApproval> pending = [];
        foreach (var entry in queue.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("requestId", out var id)
                || id.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("toolCall", out var call)
                || call.ValueKind != JsonValueKind.Object
                || !call.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            using var empty = JsonDocument.Parse("{}");
            var arguments = call.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                ? args.Clone()
                : empty.RootElement.Clone();

            pending.Add(new PendingApproval(
                id.GetString()!,
                name.GetString()!,
                arguments));
        }

        return pending;
    }

    /// <summary>Builds the answer message for one pending request.</summary>
    /// <param name="bag">What <c>AgentSessionStateBag.Serialize</c> produced.</param>
    /// <param name="requestId">The id the caller answers.</param>
    /// <param name="approved">Whether the tool may run.</param>
    /// <returns>The user message carrying the approval response, or <see langword="null"/> when no queued request carries that id.</returns>
    internal static ChatMessage? AnswerFor(JsonElement bag, string requestId, bool approved)
    {
        if (!bag.TryGetProperty(PendingStateKey, out var queue)
            || queue.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in queue.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("requestId", out var id)
                || id.GetString() != requestId
                || !entry.TryGetProperty("toolCall", out var call)
                || call.ValueKind != JsonValueKind.Object
                || !call.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || !call.TryGetProperty("callId", out var callId)
                || callId.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
            if (call.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
            {
                foreach (var argument in args.EnumerateObject())
                {
                    arguments[argument.Name] = ValueOf(argument.Value);
                }
            }

            var toolCall = new FunctionCallContent(callId.GetString()!, name.GetString()!, arguments);
            return new ChatMessage(ChatRole.User, [new ToolApprovalResponseContent(requestId, approved, toolCall)]);
        }

        return null;
    }

    private static object? ValueOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ValueOf(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ValueOf).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}

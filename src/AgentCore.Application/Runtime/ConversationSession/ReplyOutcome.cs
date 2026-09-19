using AgentCore.Domain;

namespace AgentCore.Application.Runtime;

/// <summary>What the turn said, what the caller heard, and why they differ if they do.</summary>
/// <param name="Reply">The text the record holds: what the caller heard.</param>
/// <param name="SpokenReply">The text the model produced, before any barge-in cut it.</param>
/// <param name="Failure">The section 8.7 reason, or <see langword="null"/>.</param>
/// <param name="ToolFault">The row-six fault, from the run or from the fallback layer.</param>
/// <param name="InterruptedAfter">The played duration of a cut reply, or <see langword="null"/>.</param>
/// <param name="Approvals">The tool calls still waiting on a human answer.</param>
internal sealed record ReplyOutcome(
    string Reply,
    string SpokenReply,
    string? Failure,
    string? ToolFault,
    TimeSpan? InterruptedAfter,
    List<PendingApproval> Approvals);

using AgentCore.Domain;

namespace AgentCore.Application.Runtime
{
    /// <summary>What the turn said, what the caller heard, and why they differ if they do.</summary>
    /// <param name="Reply">The text the record holds: what the caller heard.</param>
    /// <param name="GeneratedText">
    /// The text the model produced across every step, before any cut. Only a cut turn's <c>turn.completed</c> proves it:
    /// every other turn proves the words its rows say.
    /// </param>
    /// <param name="Failure">The section 8.7 reason, or <see langword="null"/>.</param>
    /// <param name="ToolFault">The message of the fault a tool let out (row six), or <see langword="null"/> when no tool did.</param>
    /// <param name="InterruptedAfter">The played duration of a cut reply, or <see langword="null"/>.</param>
    /// <param name="Approvals">The tool calls still waiting on a human answer.</param>
    /// <param name="Fault">
    /// The fault that ended the run, live and not flattened to a message: the Error log needs the object, for its
    /// stack trace.
    /// </param>
    /// <param name="IsToolFault">
    /// Whether a tool call let <paramref name="Fault"/> out (<see cref="ToolFaultMark"/>): a tool that spent its retry
    /// budget. Otherwise the run faulted outside every tool, say because the model endpoint did not answer, whether
    /// <see cref="FallbackAgent"/> caught it or it reached the turn from above that layer.
    /// </param>
    internal sealed record ReplyOutcome(
        string Reply,
        string GeneratedText,
        string? Failure,
        string? ToolFault,
        TimeSpan? InterruptedAfter,
        List<PendingApproval> Approvals,
        Exception? Fault = null,
        bool IsToolFault = false);
}

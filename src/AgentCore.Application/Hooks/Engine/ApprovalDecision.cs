namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>What the BeforeToolApproval hook chain decided about one tool call.</summary>
    /// <param name="Approved"><see langword="true"/> approved, <see langword="false"/> denied, <see langword="null"/> left to the patterns or a human.</param>
    /// <param name="Reason">The denial's reason, or <see langword="null"/>.</param>
    internal sealed record ApprovalDecision(bool? Approved, string? Reason);
}

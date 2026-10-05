using AgentCore.Domain.Audit;

namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>One tool call ended. Raised once per call.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="ToolName">The tool's name.</param>
    /// <param name="CallId">The function call id.</param>
    /// <param name="Duration">How long the call took.</param>
    /// <param name="Outcome">How it ended.</param>
    /// <param name="Fatal">Whether the fault was one the model cannot answer, so the turn speaks the fallback. False for every outcome but <see cref="ToolOutcome.Failed"/>.</param>
    /// <param name="FailureKind">The kind of failure, or <see langword="null"/> when the call did not fail.</param>
    /// <param name="Failure">The failure message, or <see langword="null"/> when the call did not fail.</param>
    public sealed record ToolCalled(
        HookScope Scope,
        string ToolName,
        string CallId,
        TimeSpan Duration,
        ToolOutcome Outcome,
        bool Fatal,
        ToolFailureKind? FailureKind,
        string? Failure)
        : HookNotice(Scope);
}

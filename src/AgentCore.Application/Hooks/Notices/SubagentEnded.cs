namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>An agent-as-tool run ended.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="ParentToolCallId">
    /// The function call id of the tool call that started it, or <see langword="null"/>. For an agent an agent-as-tool
    /// started (depth 2 or more), it is the id of the outermost tool call, the one the turn's own agent made.
    /// </param>
    /// <param name="AgentId">The agent that ran as the tool.</param>
    /// <param name="Outcome">How the run ended.</param>
    /// <param name="Duration">How long the run took.</param>
    public sealed record SubagentEnded(
        HookScope Scope,
        string? ParentToolCallId,
        string AgentId,
        SubagentOutcome Outcome,
        TimeSpan Duration)
        : HookNotice(Scope);
}

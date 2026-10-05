namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A call was accepted and its session is open.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="CallId">The transport's own id of the call.</param>
    /// <param name="From">The caller's number, or <see langword="null"/>.</param>
    /// <param name="To">The number called, or <see langword="null"/>.</param>
    /// <param name="Transport">The kind of transport that carried the call.</param>
    public sealed record CallStarted(
        HookScope Scope,
        string CallId,
        string? From,
        string? To,
        string Transport)
        : HookNotice(Scope);
}

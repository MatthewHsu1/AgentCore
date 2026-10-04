namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>
    /// A started call left, once per call, however it left. It never replaces <see cref="ConversationEnded"/>, which
    /// stays once per conversation; this one also reaches the hooks after that end.
    /// </summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="CallId">The transport's own id of the call.</param>
    /// <param name="Seconds">How long the call lasted, in seconds, up to when it began to leave.</param>
    /// <param name="Cause">The transport's word for why the call ended, or <see langword="null"/>.</param>
    /// <param name="Reason">How the call left its conversation.</param>
    public sealed record CallEnded(
        HookScope Scope,
        string CallId,
        double Seconds,
        string? Cause,
        CallEndReason Reason)
        : HookNotice(Scope);
}

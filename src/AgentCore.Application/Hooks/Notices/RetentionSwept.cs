namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The hourly retention sweep ran.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Deleted">How many rows the sweep deleted.</param>
    public sealed record RetentionSwept(HookScope Scope, int Deleted) : HookNotice(Scope);
}

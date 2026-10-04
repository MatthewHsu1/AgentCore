namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Shutdown began.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    public sealed record HostStopping(HookScope Scope) : HookNotice(Scope);
}

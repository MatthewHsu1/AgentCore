namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Boot finished: the host is ready to serve.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Entries">The names of the entries the host serves.</param>
    /// <param name="ToolCount">How many tools the host registered.</param>
    public sealed record HostStarted(HookScope Scope, IReadOnlyList<string> Entries, int ToolCount) : HookNotice(Scope);
}

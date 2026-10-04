namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The stage machine moved.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Before">The stage before.</param>
    /// <param name="After">The stage after.</param>
    public sealed record StageChanged(HookScope Scope, string Before, string After) : HookNotice(Scope);
}

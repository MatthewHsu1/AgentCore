namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A non-fatal fault happened.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Kind">Which fault.</param>
    /// <param name="Message">What went wrong.</param>
    /// <param name="Cause">The exception behind the failure, for a log's stack trace, or null.</param>
    public sealed record Fault(HookScope Scope, FaultKind Kind, string Message, Exception? Cause) : HookNotice(Scope);
}

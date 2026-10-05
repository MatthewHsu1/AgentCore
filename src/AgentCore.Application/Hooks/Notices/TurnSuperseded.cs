namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Edit-and-resend withdrew turns.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="WithdrewFrom">The first turn index withdrawn.</param>
    /// <param name="WithdrewThrough">The last turn index withdrawn.</param>
    public sealed record TurnSuperseded(HookScope Scope, int WithdrewFrom, int WithdrewThrough) : HookNotice(Scope);
}

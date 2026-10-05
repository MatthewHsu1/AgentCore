namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A spoken line closed.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Speaker">Who said it.</param>
    /// <param name="Text">The words said. The text is the caller's conversation text: a hook that logs it logs personal data.</param>
    /// <param name="StartedAt">When the line began.</param>
    /// <param name="EndedAt">When the line ended.</param>
    public sealed record LineSpoken(
        HookScope Scope,
        Speaker Speaker,
        string Text,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt)
        : HookNotice(Scope);
}

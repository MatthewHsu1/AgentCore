namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A turn was refused or dropped, so none of its words were kept.</summary>
    /// <param name="Scope">Where and when it happened. <see cref="HookScope.TurnIndex"/> is null when the turn was
    /// refused before it read the conversation.</param>
    /// <param name="Reason">Why. <see cref="TurnRefusalTokens.ToToken"/> names it for an audit row.</param>
    /// <param name="AfterEnd">
    /// Whether the conversation had already ended. Such a refusal still reaches the hooks, but no audit row follows
    /// <c>conversation.ended</c>.
    /// </param>
    public sealed record TurnRefused(HookScope Scope, TurnRefusal Reason, bool AfterEnd) : HookNotice(Scope);
}

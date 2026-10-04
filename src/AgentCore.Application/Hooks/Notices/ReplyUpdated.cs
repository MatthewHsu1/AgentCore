namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>An output update left the turn. Raised only when a hook overrides its handler.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Text">The update's text. The text is the caller's conversation text: a hook that logs it logs personal data.</param>
    /// <param name="Author">The update's author, or <see langword="null"/>.</param>
    public sealed record ReplyUpdated(HookScope Scope, string Text, string? Author) : HookNotice(Scope);
}

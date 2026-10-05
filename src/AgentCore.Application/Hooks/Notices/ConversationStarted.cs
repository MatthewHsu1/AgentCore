namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The store opened the conversation.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Origin">How the session found the conversation.</param>
    public sealed record ConversationStarted(HookScope Scope, ConversationOrigin Origin) : HookNotice(Scope);
}

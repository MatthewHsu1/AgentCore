namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The session left memory by an idle unload or a close; the conversation lives on in the store.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Cause">Why the session left memory.</param>
    public sealed record ConversationUnloaded(HookScope Scope, UnloadCause Cause) : HookNotice(Scope);
}

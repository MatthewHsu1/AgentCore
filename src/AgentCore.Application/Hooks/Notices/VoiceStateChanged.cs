namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The user's or the agent's voice state changed.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Party">Whose state changed.</param>
    /// <param name="Old">The state before.</param>
    /// <param name="New">The state after.</param>
    public sealed record VoiceStateChanged(
        HookScope Scope,
        VoiceParty Party,
        VoiceState Old,
        VoiceState New)
        : HookNotice(Scope);
}

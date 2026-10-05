using AgentCore.Application.Hooks.Gates;

namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The turn was admitted and its agent chosen.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="AgentId">The agent that runs the turn.</param>
    /// <param name="UserText">
    /// The caller's words as received, before any <see cref="AgentHook.BeforeTurnAsync"/> hook ran: a
    /// <see cref="TurnGate.ReplaceInput"/> does not change a <see cref="TurnStarted"/> already raised, so a hook that
    /// redacts input does not redact this. The text is the caller's conversation text: a hook that logs it logs personal data.
    /// </param>
    public sealed record TurnStarted(HookScope Scope, string AgentId, string UserText) : HookNotice(Scope);
}

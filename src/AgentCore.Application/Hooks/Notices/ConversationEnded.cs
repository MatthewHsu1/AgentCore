using AgentCore.Domain.Audit;

namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The conversation ended. Raised once per conversation.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Reason">Why it ended.</param>
    /// <param name="TerminalStage">The terminal stage the machine stopped in, or null when the conversation ended for another reason.</param>
    /// <param name="Call">The call that carried the conversation, or null. Set by the phone path.</param>
    public sealed record ConversationEnded(
        HookScope Scope,
        ConversationEndReason Reason,
        string? TerminalStage,
        CallEnd? Call)
        : HookNotice(Scope);
}

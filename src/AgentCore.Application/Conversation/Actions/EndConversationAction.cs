using AgentCore.Domain.Audit;

namespace AgentCore.Application.Conversation.Actions
{
    /// <summary>
    /// The same end as <c>ConversationSession.EndConversation</c>, as an action, so a tool ends its conversation through
    /// the one door it uses for every other action.
    /// </summary>
    /// <param name="Reason">Stored as the <c>conversation.ended</c> token, so it becomes part of the audit record.</param>
    public sealed record EndConversationAction(ConversationEndReason Reason) : ConversationAction;
}

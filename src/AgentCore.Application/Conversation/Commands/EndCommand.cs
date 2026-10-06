using AgentCore.Domain.Audit;

namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>
    /// The same end as <c>ConversationSession.EndConversation</c>, as a command.
    /// </summary>
    /// <param name="Reason">Stored as the <c>conversation.ended</c> token, so it becomes part of the audit record.</param>
    public sealed record EndCommand(ConversationEndReason Reason) : ChannelCommand;
}

using AgentCore.Application.Conversation.Commands;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>Implemented by the transport, because only the transport can carry out a command such as a transfer.</summary>
    internal interface IConversationChannel
    {
        /// <summary>Must not block: a tool call is waiting on it, and the command itself runs when it says.</summary>
        ChannelCommandResult Send(ChannelCommand command);
    }
}

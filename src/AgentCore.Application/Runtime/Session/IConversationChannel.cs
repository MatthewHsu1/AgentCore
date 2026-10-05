using AgentCore.Application.Conversation.Actions;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>Implemented by the transport, because only the transport can perform an action such as a transfer.</summary>
    internal interface IConversationChannel
    {
        /// <summary>Must not block: a tool call is waiting on it, and the action itself runs after the answer was delivered.</summary>
        ConversationActionResult Request(ConversationAction action);
    }
}

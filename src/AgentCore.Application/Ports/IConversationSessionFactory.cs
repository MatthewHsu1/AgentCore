using AgentCore.Application.Conversation;
using AgentCore.Application.Runtime;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// Creates one session for one conversation.
    /// </summary>
    public interface IConversationSessionFactory
    {
        /// <summary>Creates the session of one conversation.</summary>
        ConversationSession Create(string? conversationId = null, ConversationSessionState? state = null);
    }
}

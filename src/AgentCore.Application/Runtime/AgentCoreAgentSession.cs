using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime
{
    /// <summary>One conversation, as the framework sees it: the session <see cref="AgentCoreAgent"/> creates.</summary>
    internal sealed class AgentCoreAgentSession : AgentSession
    {
        internal AgentCoreAgentSession(ConversationSession conversation)
        {
            Conversation = conversation;
        }

        /// <summary>Gets the conversation this session carries.</summary>
        internal ConversationSession Conversation { get; }

        /// <inheritdoc />
        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceKey is null && serviceType.IsInstanceOfType(Conversation)
                ? Conversation
                : base.GetService(serviceType, serviceKey);
        }
    }
}

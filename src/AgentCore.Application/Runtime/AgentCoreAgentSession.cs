using AgentCore.Application.Ports;
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

        /// <summary>Gets the live conversation this session carries, as of its last <see cref="ResolveAsync"/>.</summary>
        internal ConversationSession Conversation { get; private set; }

        /// <summary>
        /// Asks the owner for this session's conversation again, under its own id. A live session comes back
        /// touched; an unloaded one comes back rebuilt, with its history intact. Either way this session's
        /// <see cref="Conversation"/> is updated to the one returned, so a later <c>GetService</c> reads the
        /// current live session.
        /// </summary>
        internal async ValueTask<ConversationSession> ResolveAsync(
            IConversationSessions sessions, string entry, CancellationToken cancellationToken)
        {
            Conversation = await sessions
                .GetOrOpenAsync(entry, Conversation.ConversationId, state: null, cancellationToken)
                .ConfigureAwait(false);

            return Conversation;
        }

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

using AgentCore.Application.Conversation.Commands;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>
    /// The end is handled here and not by the channel, because every channel already follows an end the engine asks for.
    /// Only a command the transport must carry out itself goes to the channel.
    /// </summary>
    internal sealed class ChannelCommands(ConversationSession session) : IChannelControl
    {
        private IConversationChannel? _channel;

        /// <summary>Replaces any earlier channel, because a call that takes a conversation over carries its commands from then on.</summary>
        internal void Attach(IConversationChannel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            Volatile.Write(ref _channel, channel);
        }

        /// <inheritdoc />
        public ChannelCommandResult Send(ChannelCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            
            if (session.IsComplete || session.Lifetime.Ending.Requested)
            {
                return ChannelCommandResult.Ending;
            }

            return command switch
            {
                EndCommand end => session.Lifetime.EndConversation(end.Reason) ? ChannelCommandResult.Scheduled : ChannelCommandResult.Ending,
                _ => Volatile.Read(ref _channel)?.Send(command) ?? ChannelCommandResult.NotSupported,
            };
        }
    }
}

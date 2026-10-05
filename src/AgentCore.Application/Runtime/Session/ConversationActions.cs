using AgentCore.Application.Conversation.Actions;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>
    /// The end is handled here and not by the channel, because every channel already follows an end the engine asks for.
    /// Only an action the transport must perform itself goes to the channel.
    /// </summary>
    internal sealed class ConversationActions(ConversationSession session) : IConversationControl
    {
        private IConversationChannel? _channel;

        /// <summary>Replaces any earlier channel, because a call that takes a conversation over carries its actions from then on.</summary>
        internal void Attach(IConversationChannel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            Volatile.Write(ref _channel, channel);
        }

        /// <inheritdoc />
        public ConversationActionResult Request(ConversationAction action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (session.IsComplete || session.Lifetime.Ending.Requested)
            {
                return ConversationActionResult.Ending;
            }

            return action switch
            {
                EndConversationAction end => session.Lifetime.EndConversation(end.Reason) ? ConversationActionResult.Scheduled : ConversationActionResult.Ending,
                _ => Volatile.Read(ref _channel)?.Request(action) ?? ConversationActionResult.NotSupported,
            };
        }
    }
}

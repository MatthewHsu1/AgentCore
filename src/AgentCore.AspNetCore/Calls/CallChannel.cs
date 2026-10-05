using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>
    /// The vendor's actions, such as a transfer, on the call's conversation. It follows the call to a session reopened
    /// after an idle unload, because the tools of the next ask run on that session.
    /// </summary>
    internal sealed class CallChannel
    {
        private IConversationChannel? _channel;

        internal void Use(IConversationChannel channel, ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentNullException.ThrowIfNull(session);
            Volatile.Write(ref _channel, channel);
            session.Actions.Attach(channel);
        }

        internal void Follow(ConversationSession reopened)
        {
            if (Volatile.Read(ref _channel) is { } channel)
            {
                reopened.Actions.Attach(channel);
            }
        }
    }
}

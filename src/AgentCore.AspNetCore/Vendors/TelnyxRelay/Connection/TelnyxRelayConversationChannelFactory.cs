
using AgentCore.AspNetCore.Voice.Routing;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection
{
    /// <summary>Opens a Telnyx relay channel over the two halves of one connection.</summary>
    /// <param name="input">The caller half of the connection that accepted this conversation's socket.</param>
    /// <param name="output">The reply half of the same connection.</param>
    internal sealed class TelnyxRelayConversationChannelFactory(TelnyxRelayInput input, TelnyxRelayOutput output)
        : IConversationChannelFactory
    {
        /// <inheritdoc />
        public ValueTask<ConversationChannel> OpenAsync(
            ConversationChannelContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new ConversationChannel(input, output));
        }
    }
}

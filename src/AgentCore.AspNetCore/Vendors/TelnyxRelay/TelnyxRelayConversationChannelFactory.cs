using AgentCore.AspNetCore.Voice;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay
{
    /// <summary>Opens a Telnyx relay channel over the two halves of one connection.</summary>
    /// <param name="input">The caller half of the connection that accepted this conversation's socket.</param>
    /// <param name="output">The reply half of the same connection.</param>
    internal sealed class TelnyxRelayConversationChannelFactory(TelnyxRelayInput input, TelnyxRelayOutput output)
        : IConversationChannelFactory
    {
        /// <inheritdoc />
        /// <remarks>
        /// <paramref name="context"/> is read for nothing, and the conversation id in it least of all: this
        /// vendor names the conversation on its own setup frame, and the connection is already answering that
        /// conversation by the time anything here could run. A future factory that really does open a conversation —
        /// the split adapter's — is where those values start to matter.
        /// </remarks>
        public ValueTask<ConversationChannel> OpenAsync(
            ConversationChannelContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new ConversationChannel(input, output));
        }
    }
}

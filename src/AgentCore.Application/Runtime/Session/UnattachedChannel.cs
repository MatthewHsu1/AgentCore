using AgentCore.Application.Conversation.Commands;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>Keeps <c>ToolCallScope.Channel</c> non-null, so a tool never checks for null; outside a turn there is nothing to act on.</summary>
    internal sealed class UnattachedChannel : IChannelControl
    {
        internal static readonly UnattachedChannel Instance = new();

        private UnattachedChannel()
        {
        }

        /// <inheritdoc />
        public ChannelCommandResult Send(ChannelCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            return ChannelCommandResult.NotSupported;
        }
    }
}

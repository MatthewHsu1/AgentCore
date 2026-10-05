using AgentCore.Application.Conversation.Actions;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>Keeps <c>ToolCallScope.Conversation</c> non-null, so a tool never checks for null; outside a turn there is nothing to act on.</summary>
    internal sealed class UnattachedConversationControl : IConversationControl
    {
        internal static readonly UnattachedConversationControl Instance = new();

        private UnattachedConversationControl()
        {
        }

        /// <inheritdoc />
        public ConversationActionResult Request(ConversationAction action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return ConversationActionResult.NotSupported;
        }
    }
}

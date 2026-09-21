using AgentCore.Application.Providers;

namespace AgentCore.AspNetCore.Conversation
{
    /// <summary>The names the conversation seam uses in every failure it raises.</summary>
    internal static class ConversationSeams
    {
        /// <summary>The <c>providers.conversation</c> seam.</summary>
        public static readonly VendorSeam Conversation =
            new("providers.conversation", "/providers/conversation/kind", "options.UseConversation(...)");
    }
}

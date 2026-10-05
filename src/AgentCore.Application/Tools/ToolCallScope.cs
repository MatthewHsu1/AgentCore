using AgentCore.Application.Conversation.Actions;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tools
{
    /// <summary>The conversation a bound tool runs in. A binding declares a parameter of this type to receive it; the model never sees that parameter.</summary>
    /// <param name="ConversationId">The conversation the turn belongs to.</param>
    /// <param name="TurnIndex">The zero-based index of the turn now running.</param>
    /// <param name="Stage">The stage the conversation's state machine holds. Empty when the document declares no policy.</param>
    /// <param name="Workspace">The conversation's folder on disk, or <see langword="null"/> when the host bound no workspace root.</param>
    public sealed record ToolCallScope(string ConversationId, int TurnIndex, string Stage, string? Workspace = null)
    {
        /// <summary>
        /// Gets the turn's shared state: the same dictionary a hook's gate exposes as <c>Items</c>. It lives for one
        /// turn. Durable data belongs in <c>ConversationRecord.Custom</c>.
        /// </summary>
        public IDictionary<string, object?>? Items { get; init; }

        /// <summary>
        /// Gets the conversation's action door. It sits on the scope because the binder already hands every tool the
        /// scope, so a tool needs no new parameter. Outside a turn it refuses every action: there is no conversation.
        /// </summary>
        public IConversationControl Conversation { get; init; } = UnattachedConversationControl.Instance;
    }
}

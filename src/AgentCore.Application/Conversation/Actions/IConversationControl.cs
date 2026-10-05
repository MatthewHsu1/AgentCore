namespace AgentCore.Application.Conversation.Actions
{
    /// <summary>
    /// One door for every action, so a new action needs no new method here and no new parameter on a tool.
    /// </summary>
    public interface IConversationControl
    {
        /// <summary>Returns at once and runs nothing inline, because an action must wait until the current answer was delivered.</summary>
        /// <param name="action">One of the actions AgentCore defines.</param>
        /// <returns>Whether the action will run, and if not, why, so the tool can tell the model.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
        ConversationActionResult Request(ConversationAction action);
    }
}

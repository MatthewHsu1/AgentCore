namespace AgentCore.Application.Transcript
{
    /// <summary>What one rewrite of a committed reply did to its rows.</summary>
    /// <param name="Rewritten">The rows whose content changed in place.</param>
    /// <param name="Removed">The rows the cut left carrying nothing, which the conversation no longer holds.</param>
    internal sealed record ReplyRewrite(IReadOnlyList<ConversationMessage> Rewritten, IReadOnlyList<ConversationMessage> Removed)
    {
        /// <summary>Gets whether the rewrite changed any row.</summary>
        public bool Changed => Rewritten.Count > 0 || Removed.Count > 0;
    }
}

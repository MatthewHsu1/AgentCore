namespace AgentCore.Application.Transcript
{
    /// <summary>One turn's commit: the ids its append wrote, and whether the store kept them.</summary>
    /// <param name="UserMessageId">What the user's message was written under.</param>
    /// <param name="ReplyMessageId">What the last message was written under, or <see langword="null"/> when the user's message is all the turn wrote.</param>
    /// <param name="Spoken">The words the append's rows say, as <see cref="TurnWords.Spoken"/> reads them.</param>
    /// <param name="Refused">
    /// Completes once the append landed or was dropped: <see langword="true"/> when the store refused it because the
    /// conversation already saved this turn.
    /// </param>
    internal sealed record TurnWrite(string UserMessageId, string? ReplyMessageId, string Spoken, Task<bool> Refused)
    {
        /// <summary>Gets the two ids together.</summary>
        public (string UserMessageId, string? ReplyMessageId) Ids => (UserMessageId, ReplyMessageId);
    }
}

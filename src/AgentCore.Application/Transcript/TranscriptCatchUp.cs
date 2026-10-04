using AgentCore.Application.Conversation;

namespace AgentCore.Application.Transcript
{
    /// <summary>What the message store held beyond a session's words, read without changing them.</summary>
    /// <param name="Revision">The transcript's revision when the read began. The rows apply only while it stands.</param>
    /// <param name="Rows">The rows the message store read for the session.</param>
    /// <param name="NextOrdinal">The next free ordinal of the conversation, from the conversation store's own counter.</param>
    /// <param name="State">The state the conversation store holds, or <see langword="null"/> when it holds none.</param>
    internal sealed record TranscriptCatchUp(int Revision, IReadOnlyList<ConversationMessage> Rows, int NextOrdinal, ConversationSessionState? State)
    {
        /// <summary>Gets the index the conversation's next turn takes, from the state the conversation store holds.</summary>
        public int NextTurnIndex => State?.NextTurnIndex ?? 0;

        /// <summary>Gets the last of the session's lost writes counted before the read. A catch-up that takes these rows clears it and every one before it.</summary>
        public int LossMark { get; init; }

        /// <summary>
        /// Gets whether the store lost a write of this session and holds another session's writes in its place. The
        /// session's own state then no longer follows from what the store holds.
        /// </summary>
        public bool Overtaken { get; init; }
    }
}

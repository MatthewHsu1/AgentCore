using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>One stored message of one conversation. It is one row of the message store.</summary>
    /// <param name="ConversationId">The conversation the message belongs to.</param>
    /// <param name="Ordinal">
    /// The message's position within the conversation. It is monotonic and never reused. It is not dense: an
    /// edit deletes the rows it replaces and the ordinals they held are not issued again, because
    /// the audit store still holds audit rows against the turns those ordinals belonged to.
    /// </param>
    /// <param name="TurnIndex">The turn the message belongs to. It is the join to the audit chain.</param>
    /// <param name="Content">The message itself.</param>
    /// <param name="MessageId">What this message is called. Unique within the conversation.
    /// <para>
    /// It is the handle an edit names its parent by, and nothing else — AgentCore reads it for that and
    /// gives it no other meaning. A caller's own id is kept when one arrives, and one is minted when
    /// none does, because the parent an edit names is usually a reply, and a reply has no id until this
    /// host gives it one.
    /// </para>
    /// </param>
    public sealed record ConversationMessage(
        string ConversationId, int Ordinal, int TurnIndex, ChatMessage Content, string MessageId)
    {
        /// <summary>
        /// Gets the last ordinal this row stands in for, when it is a summary the session wrote in place
        /// of every row at or below it; <see langword="null"/> for a row somebody said. Every consumer
        /// read of the message store leaves summary rows out; only the session's own read returns one.
        /// </summary>
        public int? CoversUpTo { get; init; }
    }
}

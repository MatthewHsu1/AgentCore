using AgentCore.Application.Runtime;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// Reads the facts of a conversation as the turn loop raises them. It never sits on the turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one seam between the turn loop and everything that watches it. The audit chain of D23,
    /// the counters of section 8.6, and the "log once" rows of section 8.7 are three implementations of
    /// this port, and <see cref="ConversationSession"/> knows none of them: it raises a
    /// <see cref="ConversationEvent"/> and moves on.
    /// </para>
    /// <para>
    /// The contract of <see cref="IAuditSinkPort"/> applies here in full, and for the same measurement.
    /// Section 7 puts a durable insert at 13 ms at p50 and 32 ms at p99, against 91 nanoseconds to
    /// enqueue, so an observer completes when it has ACCEPTED the fact and never when it has stored it.
    /// An implementation that awaits a database, an HTTP call, or a disk inside
    /// <see cref="OnConversationEventAsync"/> has broken the contract, and the turn loop pays for it.
    /// </para>
    /// <para>
    /// The dispatcher behind this port never lets an observer end a turn: a fault is logged once and
    /// swallowed, and an observer that does not complete at once is observed off-turn. An implementation
    /// may therefore throw, and nothing that throws is retried. Observers of one conversation are also invoked in
    /// the order the conversation raised the facts, so a late fact never overtakes an earlier one.
    /// </para>
    /// <para>
    /// One instance serves every conversation, because a <see cref="ConversationEvent"/> names its own conversation. Nothing here
    /// is per conversation, and an implementation that holds per-call state must key it by
    /// <see cref="ConversationEvent.ConversationId"/>.
    /// </para>
    /// </remarks>
    public interface IConversationObserver
    {
        /// <summary>Takes one fact about a conversation.</summary>
        /// <param name="conversationEvent">What happened. Nothing edits it afterwards.</param>
        /// <param name="cancellationToken">
        /// Cancels the acceptance, and never the work behind it. The dispatcher passes
        /// <see cref="CancellationToken.None"/>: the record belongs to the conversation, and not to a turn the
        /// caller may have cancelled.
        /// </param>
        /// <returns>
        /// A task that completes when the observer has accepted the fact. It is not stored yet.
        /// </returns>
        ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken);
    }
}

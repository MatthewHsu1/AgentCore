namespace AgentCore.Domain
{
    /// <summary>
    /// What one finished turn did to one conversation.
    /// </summary>
    /// <param name="ConversationId">The id of the conversation this turn belongs to.</param>
    /// <param name="TurnIndex">The zero-based index of the turn that just ran.</param>
    /// <param name="StageBefore">
    /// The stage the turn ran in. It is empty when the document declares no <c>policy:</c>.
    /// </param>
    /// <param name="StageAfter">
    /// The stage the machine holds after the turn. It equals <paramref name="StageBefore"/> when no exit
    /// guard is true, and when the document declares no <c>policy:</c>.
    /// </param>
    /// <param name="ReplyText">
    /// The text the caller heard. It is what the agent spoke, the spoken fallback when
    /// <paramref name="Failure"/> is set, and the truncated reply when
    /// <paramref name="Cut"/> is set.
    /// </param>
    /// <param name="IsTerminal">
    /// Whether the conversation is over after this turn: the stage after it is terminal, or a host or a tool
    /// ended the conversation while it ran.
    /// </param>
    /// <param name="ExtractionFailure">
    /// The reason the extractor produced nothing, or <see langword="null"/> when it ran or did not run.
    /// </param>
    /// <param name="Failure">
    /// The reason the turn spoke the fallback instead of a reply, or <see langword="null"/> when the turn
    /// answered.
    /// </param>
    /// <param name="Cut">
    /// <see langword="null"/> when the reply was not cut. A value when it was: how long the caller heard
    /// the reply, as the relay reports it at 1 ms. <see cref="TimeSpan.Zero"/> also means the play time is
    /// unknown, as on every text abort. The audit record keeps the real "unknown": its
    /// <c>reply.interrupted</c> event then carries no <c>durationUntilInterruptMs</c>.
    /// </param>
    /// <param name="EndedAt">
    /// The moment the turn ended, read from the clock the turn loop was given. The audit chain
    /// stamps <c>occurred_at</c> from it. The sink runs behind a bounded <c>Channel</c> and a background
    /// writer, so a writer that read its own clock would stamp the event one enqueue late and a slow
    /// queue would move the record of the conversation. The turn loop reads the clock once, when the turn ends,
    /// and every event of that turn carries the same moment. A record a caller builds by hand and leaves
    /// unset reads <c>default</c>.
    /// </param>
    public sealed record TurnResult(
        string ConversationId,
        int TurnIndex,
        string StageBefore,
        string StageAfter,
        string ReplyText,
        bool IsTerminal,
        string? ExtractionFailure,
        string? Failure = null,
        TimeSpan? Cut = null,
        DateTimeOffset EndedAt = default)
    {
        /// <summary>
        /// Gets the tool calls still waiting on a human answer when the turn ended, oldest first.
        /// Empty on every other turn. A pending turn is not a failure: <see cref="Failure"/> stays
        /// <see langword="null"/> and <see cref="ReplyText"/> stays empty while these are set.
        /// </summary>
        public IReadOnlyList<PendingApproval> Approvals { get; init; } = [];
    }
}

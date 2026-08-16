namespace AgentCore.Application.Runtime;

/// <summary>
/// The closed set of facts one call raises to its observers.
/// </summary>
/// <remarks>
/// <para>
/// This is the vocabulary of the hook, and it is deliberately LARGER than
/// <see cref="Domain.Audit.AuditEventKind"/>. Four of the kinds here are diagnostic only: they are
/// counted and logged, and no audit row records them. A turn loop that raised only what the chain
/// stores would have to keep its own logging beside the hook, which is the coupling this seam exists
/// to remove.
/// </para>
/// <para>
/// The two enums are therefore NOT interchangeable, and nothing casts between them.
/// <c>AuditCallObserver</c> owns the mapping, and it drops every kind the chain does not hold.
/// </para>
/// <para>
/// Unlike <see cref="Domain.Audit.AuditEventKind"/>, no number here is ever stored or hashed. A
/// <see cref="CallEvent"/> lives for the length of one dispatch and reaches nothing but an observer
/// in this process, so the numeric values carry no compatibility promise and the wire token of an
/// audit row is produced by <see cref="Domain.Audit.AuditEventKinds"/> from the mapped kind. The set
/// is still closed, for the reason <see cref="Domain.Audit.AuditEventKind"/> gives: a vocabulary that
/// grows by one string on every new caller cannot be read a year later.
/// </para>
/// </remarks>
public enum CallEventKind
{
    /// <summary>The call started. It is the first fact of every call.</summary>
    /// <remarks>One call raises exactly one of these, and it carries no turn index. The chain stores it.</remarks>
    CallStarted = 0,

    /// <summary>Moderation flagged what the CALLER said, so the agent refused to answer that turn.</summary>
    /// <remarks>
    /// The verdict is known BEFORE the model runs, so this fact precedes the
    /// <see cref="TurnCompleted"/> of the same turn and amends nothing. It carries the flagged
    /// categories, and the chain stores it.
    /// </remarks>
    PromptFlagged = 1,

    /// <summary>The moderation endpoint did not answer in time, or it faulted, so the turn ran unchecked.</summary>
    /// <remarks>
    /// Diagnostic only: it is counted and logged, and <see cref="CallEvent.Ordinal"/> is
    /// <see langword="null"/> so no audit row records it. Moderation guards the turn and must never
    /// be the thing that drops it, so an endpoint that cannot answer is reported and the turn goes on.
    /// </remarks>
    ModerationUnavailable = 2,

    /// <summary>The moderation endpoint answered, and it flagged nothing.</summary>
    /// <remarks>
    /// Diagnostic only, and the quietest of the four: it is counted and not even logged, and
    /// <see cref="CallEvent.Ordinal"/> is <see langword="null"/>. The clean verdict is what makes the
    /// flagged count readable as a rate, and a clean turn is not a fact about the call worth a
    /// permanent row.
    /// </remarks>
    ModerationClean = 3,

    /// <summary>A tool failed its whole retry budget, and the turn spoke the fallback instead.</summary>
    /// <remarks>A failing tool never ends a call, so this fact sits beside the turn and not instead of it. The chain stores it.</remarks>
    ToolFailed = 4,

    /// <summary>The run returned quietly with no text, so the turn spoke the fallback.</summary>
    /// <remarks>
    /// Diagnostic only: it is counted and logged, and <see cref="CallEvent.Ordinal"/> is
    /// <see langword="null"/> so no audit row records it. On a voice call the silence is the failure,
    /// so the turn loop reads the reply rather than trusting the absence of an exception. The
    /// <see cref="TurnCompleted"/> of the same turn still writes the row, and it carries the fallback
    /// the caller actually heard.
    /// </remarks>
    EmptyReply = 5,

    /// <summary>The extractor returned an invalid object, so the slots stayed unchanged.</summary>
    /// <remarks>
    /// Diagnostic only: it is counted and logged, and <see cref="CallEvent.Ordinal"/> is
    /// <see langword="null"/> so no audit row records it. The call continues, so this is a warning
    /// and never an error.
    /// </remarks>
    ExtractionFailed = 6,

    /// <summary>One turn ran to the end, and the caller heard the whole reply.</summary>
    /// <remarks>
    /// It carries the turn index, the stage the turn ran in, the stage the machine holds after it,
    /// and the reply text the model produced. The chain stores it.
    /// </remarks>
    TurnCompleted = 7,

    /// <summary>The caller spoke over a reply, so the reply stopped early.</summary>
    /// <remarks>
    /// This fact amends the <see cref="TurnCompleted"/> of the same turn, per T23, because the chain
    /// refuses to rewrite the first event. <see cref="CallEvent.AmendsOrdinal"/> is therefore required
    /// on this kind. It records the text the caller ACTUALLY HEARD and never the text the model
    /// produced. The chain stores it.
    /// </remarks>
    ReplyInterrupted = 8,

    /// <summary>The call ended. It is the last fact of every call.</summary>
    /// <remarks>One call raises exactly one of these, and it carries no turn index. The chain stores it.</remarks>
    CallEnded = 9,
}

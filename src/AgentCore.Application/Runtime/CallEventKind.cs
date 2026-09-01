namespace AgentCore.Application.Runtime;

/// <summary>
/// The closed set of facts one call raises to its observers.
/// </summary>
public enum CallEventKind
{
    /// <summary>A session of the call started. It is the first fact of every session.</summary>
    /// <remarks>
    /// One SESSION raises exactly one of these, and a call can have several of them: a chat page
    /// reload opens a second session onto the same call. It carries no turn index. The chain stores
    /// it, and <see cref="Domain.Audit.AuditEventKind.CallStarted"/> says why it is not suppressed on
    /// a resume.
    /// </remarks>
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
    /// Diagnostic only: it is counted and logged, and <see cref="CallEvent.EventId"/> is
    /// <see langword="null"/> so no audit row records it. Moderation guards the turn and must never
    /// be the thing that drops it, so an endpoint that cannot answer is reported and the turn goes on.
    /// </remarks>
    ModerationUnavailable = 2,

    /// <summary>The moderation endpoint answered, and it flagged nothing.</summary>
    /// <remarks>
    /// Diagnostic only, and the quietest of the six: it is counted and not even logged, and
    /// <see cref="CallEvent.EventId"/> is <see langword="null"/>. The clean verdict is what makes the
    /// flagged count readable as a rate, and a clean turn is not a fact about the call worth a
    /// permanent row.
    /// </remarks>
    ModerationClean = 3,

    /// <summary>A tool failed its whole retry budget, and the turn spoke the fallback instead.</summary>
    /// <remarks>A failing tool never ends a call, so this fact sits beside the turn and not instead of it. The chain stores it.</remarks>
    ToolFailed = 4,

    /// <summary>The run returned quietly with no text, so the turn spoke the fallback.</summary>
    /// <remarks>
    /// Diagnostic only: it is counted and logged, and <see cref="CallEvent.EventId"/> is
    /// <see langword="null"/> so no audit row records it. On a voice call the silence is the failure,
    /// so the turn loop reads the reply rather than trusting the absence of an exception. The
    /// <see cref="TurnCompleted"/> of the same turn still writes the row, and it carries the fallback
    /// the caller actually heard.
    /// </remarks>
    EmptyReply = 5,

    /// <summary>The extractor returned an invalid object, so the slots stayed unchanged.</summary>
    /// <remarks>
    /// Diagnostic only: it is counted and logged, and <see cref="CallEvent.EventId"/> is
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
    /// refuses to rewrite the first event. <see cref="CallEvent.AmendsEventId"/> is therefore required
    /// on this kind. It records the text the caller ACTUALLY HEARD and never the text the model
    /// produced. The chain stores it.
    /// </remarks>
    ReplyInterrupted = 8,

    /// <summary>A store 1 write was refused, so the durable copy of some words was lost.</summary>
    /// <remarks>
    /// Diagnostic only: it is logged where the write was refused, which is the only place the
    /// exception still exists, and counted nowhere — no instrument of section 8.6 takes it, because
    /// the turn did not fail. <see cref="CallEvent.EventId"/> is <see langword="null"/> so no audit
    /// row records it either. A dropped write is a fact about the system
    /// and not about the call — the caller notices nothing, the live history is unharmed, and the
    /// next turn still has the whole conversation — so it takes no identity and the chain owes it no
    /// row.
    /// </remarks>
    TranscriptWriteFailed = 10,

    /// <summary>The host ended the call. It is the last fact of the session that ended it.</summary>
    /// <remarks>
    /// One session raises at most one of these, and another session may open onto the same call
    /// afterwards, so it is not necessarily the last fact of the call. It carries no turn index. The
    /// chain stores it.
    /// </remarks>
    CallEnded = 9,

    /// <summary>A resumed call could not restore part of its stored state: the document changed under it.</summary>
    /// <remarks>
    /// Diagnostic only: it is logged, counted nowhere, and <see cref="CallEvent.EventId"/> is
    /// <see langword="null"/> so no audit row records it. It is the one diagnostic kind raised
    /// outside a turn — the call is opening — so it carries no turn index. Restore is best effort by
    /// D6, and this is what makes "best effort" reportable rather than silent: without the line, a
    /// document change that cost every call in flight its stage would reach nobody but a host that
    /// wrote an <c>ICallObserver</c> of its own.
    /// </remarks>
    StateRestorePartial = 11,
}

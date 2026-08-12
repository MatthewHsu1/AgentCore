namespace AgentCore.Domain;

/// <summary>
/// What one finished turn did to one call.
/// </summary>
/// <remarks>
/// <para>
/// The turn loop produces one of these for each turn, and the host reads it. It is a pure record and
/// it holds no live object, so it crosses a process boundary and a log line unchanged.
/// </para>
/// <para>
/// A failed extraction is reported here and never thrown. The extractor has no retry, so a turn that
/// filled no slot still ends, the machine still picks a stage, and the caller still hears the reply.
/// </para>
/// <para>
/// A failed turn is reported the same way. Section 8.7 gives the turn loop two failures it must
/// survive: a run that returns an empty reply after 40 tool rounds, and a tool that fails four times
/// in a row. Both end the turn with a spoken fallback, both fill <see cref="Failure"/>, and neither
/// drops the call.
/// </para>
/// <para>
/// <see cref="ReplyText"/> is always the text the caller heard. It is the fallback when the turn
/// failed, and it is <c>utteranceUntilInterrupt</c> when the caller spoke over the reply. Nothing
/// here is estimated: section 7.1 says the relay reports both truncation values, so the audit row of
/// item 6a reads them and never computes them.
/// </para>
/// </remarks>
/// <param name="CallId">The id of the call this turn belongs to.</param>
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
/// <paramref name="InterruptedAfter"/> is set.
/// </param>
/// <param name="IsTerminal">Whether the stage after the turn ends the call.</param>
/// <param name="ExtractionFailure">
/// The reason the extractor produced nothing, or <see langword="null"/> when it ran or did not run.
/// </param>
/// <param name="Failure">
/// The reason the turn spoke the fallback instead of a reply, or <see langword="null"/> when the turn
/// answered. It names the row of section 8.7 that the turn met.
/// </param>
/// <param name="InterruptedAfter">
/// How long the caller heard the reply before speaking over it, or <see langword="null"/> when the
/// caller did not interrupt. The relay reports it at 1 ms, so it is never estimated.
/// </param>
/// <param name="EndedAt">
/// The moment the turn ended, read from the clock the turn loop was given. The audit chain of D23
/// stamps <c>occurred_at</c> from it. The sink runs behind a bounded <c>Channel</c> and a background
/// writer, so a writer that read its own clock would stamp the event one enqueue late and a slow
/// queue would move the record of the call. The turn loop reads the clock once, when the turn ends,
/// and every event of that turn carries the same moment. A record a caller builds by hand and leaves
/// unset reads <c>default</c>.
/// </param>
public sealed record TurnResult(
    string CallId,
    int TurnIndex,
    string StageBefore,
    string StageAfter,
    string ReplyText,
    bool IsTerminal,
    string? ExtractionFailure,
    string? Failure = null,
    TimeSpan? InterruptedAfter = null,
    DateTimeOffset EndedAt = default);

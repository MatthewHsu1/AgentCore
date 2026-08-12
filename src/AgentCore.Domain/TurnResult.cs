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
/// <param name="ReplyText">The text the agent spoke.</param>
/// <param name="IsTerminal">Whether the stage after the turn ends the call.</param>
/// <param name="ExtractionFailure">
/// The reason the extractor produced nothing, or <see langword="null"/> when it ran or did not run.
/// </param>
public sealed record TurnResult(
    string CallId,
    int TurnIndex,
    string StageBefore,
    string StageAfter,
    string ReplyText,
    bool IsTerminal,
    string? ExtractionFailure);

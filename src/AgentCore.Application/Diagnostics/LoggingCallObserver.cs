using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Diagnostics;

/// <summary>
/// Writes the "log once" rows of section 8.7, one line for each fact that earns one.
/// </summary>
/// <remarks>
/// <para>
/// Five of the ten kinds are reported, and the lines are the ones <see cref="Log"/> already declares:
/// same message, same level, same event id. Section 8.7 says "log once" for each row and each of these
/// facts is raised once for its turn, so moving the call sites behind the hook changed neither the
/// text an operator greps for nor how often it appears.
/// </para>
/// <para>
/// The five quiet kinds are quiet on purpose. <see cref="CallEventKind.CallStarted"/>,
/// <see cref="CallEventKind.TurnCompleted"/>, <see cref="CallEventKind.ReplyInterrupted"/>, and
/// <see cref="CallEventKind.CallEnded"/> are the record of a normal call, and the chain of D23 is
/// where a record of a call belongs; a line for each of them would bill Grafana Cloud by volume for
/// facts a query already answers. <see cref="CallEventKind.ModerationClean"/> is quieter still: it is
/// counted and not even logged, because the clean verdict is what makes the flagged count readable as
/// a rate and nothing more.
/// </para>
/// <para>
/// No line carries what the caller said or what the agent replied, and that is a rule and not an
/// oversight. The words behind <see cref="CallEventKind.PromptFlagged"/> are the text moderation
/// flagged, so a line carrying them would copy the content out of the chain, which D23 protects, and
/// into a log store, which it does not. The categories are reported instead.
/// </para>
/// <para>
/// It never fails and never waits, so it always completes synchronously. One instance serves every
/// call.
/// </para>
/// </remarks>
internal sealed class LoggingCallObserver : ICallObserver
{
    /// <summary>The turn a line names when the fact carried no index. No turn has it.</summary>
    /// <remarks>
    /// The five reported kinds all happen inside a turn and all carry its index, so this is
    /// unreachable for any event the session raises. A line an operator can read beats an exception
    /// thrown over a missing field, so a malformed event is reported under a value that is obviously
    /// not a turn.
    /// </remarks>
    private const int NoTurn = -1;

    private readonly ILogger _logger;

    /// <summary>Creates the observer that writes the lines of section 8.7.</summary>
    /// <param name="logger">
    /// Where the lines go, or <see langword="null"/> for <see cref="NullLogger.Instance"/>. The
    /// library never throws for want of one.
    /// </param>
    public LoggingCallObserver(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callEvent);

        switch (callEvent.Kind)
        {
            case CallEventKind.ToolFailed:
                // Section 8.7, row six. The turn spoke the fallback and the call stays alive, so
                // nothing here is fatal, and the level says Error because a spent retry budget is a
                // defect in the tool.
                Log.ToolBudgetSpent(
                    _logger,
                    callEvent.CallId,
                    TurnOf(callEvent),
                    Detail(callEvent, AuditPayloadKeys.ToolError));
                break;

            case CallEventKind.EmptyReply:
                // Section 8.7, last row. On a voice call the silence is the failure.
                Log.EmptyReply(_logger, callEvent.CallId, TurnOf(callEvent));
                break;

            case CallEventKind.ExtractionFailed:
                // Section 8.7, row two. The slots stay unchanged and the call continues, so this is a
                // warning and never an error.
                Log.ExtractionFailed(
                    _logger,
                    callEvent.CallId,
                    TurnOf(callEvent),
                    Detail(callEvent, CallEventPayloadKeys.Reason));
                break;

            case CallEventKind.PromptFlagged:
                Log.PromptRefused(
                    _logger,
                    callEvent.CallId,
                    TurnOf(callEvent),
                    Detail(callEvent, AuditPayloadKeys.ModerationCategories));
                break;

            case CallEventKind.ModerationUnavailable:
                // A vendor that did not answer is not a defect in this library, so it is a warning.
                Log.ModerationUnavailable(
                    _logger,
                    callEvent.CallId,
                    TurnOf(callEvent),
                    Detail(callEvent, CallEventPayloadKeys.Reason));
                break;

            default:
                // The chain of D23 is the record of a normal call, and a log is not.
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Reads the turn a line names.</summary>
    /// <param name="callEvent">The fact being reported.</param>
    /// <returns>Its turn index, or <see cref="NoTurn"/> when it carried none.</returns>
    private static int TurnOf(CallEvent callEvent) => callEvent.TurnIndex ?? NoTurn;

    /// <summary>Reads one detail a line reports.</summary>
    /// <param name="callEvent">The fact being reported.</param>
    /// <param name="key">The payload key the detail sits under.</param>
    /// <returns>The detail, or an empty string when the fact carried none.</returns>
    /// <remarks>
    /// A missing fact is an absent key and never the word "unknown", per the rule the turn loop keeps
    /// for a payload. The line is still written: what happened is the point of it, and the detail is
    /// the part a reader may have to do without.
    /// </remarks>
    private static string Detail(CallEvent callEvent, string key)
        => callEvent.Payload.TryGetValue(key, out string? detail) ? detail : string.Empty;
}

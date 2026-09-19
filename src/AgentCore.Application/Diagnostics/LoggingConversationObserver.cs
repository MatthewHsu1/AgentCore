using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Diagnostics;

/// <summary>
/// Writes the "log once" rows of section 8.7, one line for each fact that earns one.
/// </summary>
internal sealed class LoggingConversationObserver : IConversationObserver
{
    /// <summary>The turn a line names when the fact carried no index. No turn has it.</summary>
    private const int NoTurn = -1;

    private readonly ILogger _logger;

    /// <summary>Creates the observer that writes the lines of section 8.7.</summary>
    /// <param name="logger">
    /// Where the lines go, or <see langword="null"/> for <see cref="NullLogger.Instance"/>. The
    /// library never throws for want of one.
    /// </param>
    public LoggingConversationObserver(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversationEvent);

        switch (conversationEvent.Kind)
        {
            case ConversationEventKind.ToolFailed:
                // Section 8.7, row six. The turn spoke the fallback and the conversation stays alive, so
                // nothing here is fatal, and the level says Error because a spent retry budget is a
                // defect in the tool.
                //
                // This kind is now raised at two altitudes, and only the TURN one earns a line. A
                // fact that carries a tool call id is one failing conversation, and a turn spends up to four
                // of them; the chain of D23 keeps every one, which is where a record of a conversation
                // belongs, and section 8.7 asks for one line and not four. The turn-level fact
                // carries no conversation id, because the fault reaches the session with no function name on
                // it at all, so the absent key is exactly the test.
                if (conversationEvent.Payload.ContainsKey(AuditPayloadKeys.ToolCallId))
                {
                    break;
                }

                Log.ToolBudgetSpent(
                    _logger,
                    conversationEvent.ConversationId,
                    TurnOf(conversationEvent),
                    Detail(conversationEvent, AuditPayloadKeys.ToolError));
                break;

            case ConversationEventKind.EmptyReply:
                // Section 8.7, last row. On a voice call the silence is the failure.
                Log.EmptyReply(_logger, conversationEvent.ConversationId, TurnOf(conversationEvent));
                break;

            case ConversationEventKind.ExtractionFailed:
                // Section 8.7, row two. The slots stay unchanged and the conversation continues, so this is a
                // warning and never an error.
                Log.ExtractionFailed(
                    _logger,
                    conversationEvent.ConversationId,
                    TurnOf(conversationEvent),
                    Detail(conversationEvent, ConversationEventPayloadKeys.Reason));
                break;

            case ConversationEventKind.PromptFlagged:
                Log.PromptRefused(
                    _logger,
                    conversationEvent.ConversationId,
                    TurnOf(conversationEvent),
                    Detail(conversationEvent, AuditPayloadKeys.ModerationCategories));
                break;

            case ConversationEventKind.ModerationUnavailable:
                // A vendor that did not answer is not a defect in this library, so it is a warning.
                Log.ModerationUnavailable(
                    _logger,
                    conversationEvent.ConversationId,
                    TurnOf(conversationEvent),
                    Detail(conversationEvent, ConversationEventPayloadKeys.Reason));
                break;

            case ConversationEventKind.StateRestorePartial:
                // Not a turn's fact and not section 8.7's: it happens once, as the conversation opens, and it
                // carries no turn index. It earns a line all the same because it is the only signal
                // there is. A document change costs a resumed conversation its stage or a slot, the chain
                // stores no diagnostic kind, and the caller notices only that the agent has forgotten
                // them — so without this line the fault is visible to nobody but a host that wrote an
                // IConversationObserver of its own.
                Log.StateRestorePartial(
                    _logger,
                    conversationEvent.ConversationId,
                    Detail(conversationEvent, ConversationEventPayloadKeys.Reason));
                break;

            default:
                // The chain of D23 is the record of a normal conversation, and a log is not.
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Reads the turn a line names.</summary>
    /// <param name="conversationEvent">The fact being reported.</param>
    /// <returns>Its turn index, or <see cref="NoTurn"/> when it carried none.</returns>
    private static int TurnOf(ConversationEvent conversationEvent) => conversationEvent.TurnIndex ?? NoTurn;

    /// <summary>Reads one detail a line reports.</summary>
    /// <param name="conversationEvent">The fact being reported.</param>
    /// <param name="key">The payload key the detail sits under.</param>
    /// <returns>The detail, or an empty string when the fact carried none.</returns>
    /// <remarks>
    /// A missing fact is an absent key and never the word "unknown", per the rule the turn loop keeps
    /// for a payload. The line is still written: what happened is the point of it, and the detail is
    /// the part a reader may have to do without.
    /// </remarks>
    private static string Detail(ConversationEvent conversationEvent, string key)
        => conversationEvent.Payload.TryGetValue(key, out string? detail) ? detail : string.Empty;
}

using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Diagnostics
{
    /// <summary>
    /// Writes the "log once" rows of section 8.7, one line for each fact that earns one.
    /// </summary>
    /// <param name="logger">
    /// Where the lines go, or <see langword="null"/> for <see cref="NullLogger.Instance"/>. The
    /// library never throws for want of one.
    /// </param>
    internal sealed class LoggingConversationObserver(ILogger? logger = null) : IConversationObserver
    {
        /// <summary>The turn a line names when the fact carried no index. No turn has it.</summary>
        private const int NoTurn = -1;

        private readonly ILogger _logger = logger ?? NullLogger.Instance;

        /// <inheritdoc />
        public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(conversationEvent);

            switch (conversationEvent.Kind)
            {
                case ConversationEventKind.EmptyReply:
                    Log.EmptyReply(_logger, conversationEvent.ConversationId, TurnOf(conversationEvent));
                    break;

                case ConversationEventKind.ExtractionFailed:
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
                    Log.ModerationUnavailable(
                        _logger,
                        conversationEvent.ConversationId,
                        TurnOf(conversationEvent),
                        Detail(conversationEvent, ConversationEventPayloadKeys.Reason));
                    break;

                case ConversationEventKind.StateRestorePartial:
                    Log.StateRestorePartial(
                        _logger,
                        conversationEvent.ConversationId,
                        Detail(conversationEvent, ConversationEventPayloadKeys.Reason));
                    break;

                case ConversationEventKind.TurnRefused:
                    Log.TurnRefused(
                        _logger,
                        conversationEvent.ConversationId,
                        TurnOf(conversationEvent),
                        Detail(conversationEvent, AuditPayloadKeys.RefusedReason));
                    break;

                case ConversationEventKind.ConversationStarted:
                case ConversationEventKind.ModerationClean:
                case ConversationEventKind.TurnCompleted:
                case ConversationEventKind.ReplyInterrupted:
                case ConversationEventKind.TurnSuperseded:
                case ConversationEventKind.ConversationEnded:
                    break;

                case ConversationEventKind.TranscriptWriteFailed:
                case ConversationEventKind.TranscriptResyncFailed:
                case ConversationEventKind.ToolFailed:
                case ConversationEventKind.RunFaulted:
                    break;

                default:
                    break;
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>Reads the turn a line names.</summary>
        /// <param name="conversationEvent">The fact being reported.</param>
        /// <returns>Its turn index, or <see cref="NoTurn"/> when it carried none.</returns>
        private static int TurnOf(ConversationEvent conversationEvent)
        {
            return conversationEvent.TurnIndex ?? NoTurn;
        }

        /// <summary>Reads one detail a line reports.</summary>
        /// <param name="conversationEvent">The fact being reported.</param>
        /// <param name="key">The payload key the detail sits under.</param>
        /// <returns>The detail, or an empty string when the fact carried none.</returns>
        private static string Detail(ConversationEvent conversationEvent, string key)
        {
            return conversationEvent.Payload.TryGetValue(key, out string? detail) ? detail : string.Empty;
        }
    }
}

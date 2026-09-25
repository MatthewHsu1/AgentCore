using System.Globalization;
using AgentCore.Domain;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The event chain of one conversation. It gives every fact its identity, hands it to the observers, and
    /// closes the chain once.
    /// </summary>
    internal sealed class ConversationEventChain
    {
        private readonly string _conversationId;

        private readonly ConversationObserverDispatcher _observers;

        private readonly TimeProvider _time;

        private int _ended;

        internal ConversationEventChain(string conversationId, ConversationObserverDispatcher observers, TimeProvider time)
        {
            _conversationId = conversationId;
            _observers = observers;
            _time = time;
        }

        /// <summary>Gets whether the chain already closed. Nothing may be appended behind conversation.ended.</summary>
        internal bool HasEnded => Volatile.Read(ref _ended) == 1;

        /// <summary>Raises one durable fact of this conversation, and gives it its identity.</summary>
        internal Guid Raise(
            ConversationEventKind kind,
            DateTimeOffset occurredAt,
            int? turnIndex,
            Guid? amends = null,
            IReadOnlyDictionary<string, string>? payload = null)
        {
            Guid eventId = Guid.CreateVersion7();

            Dispatch(kind, occurredAt, turnIndex, eventId, amends, payload);

            return eventId;
        }

        /// <summary>Raises one fact that is counted and logged and stored nowhere.</summary>
        /// <param name="kind">What happened. The chain of D23 holds no row for it.</param>
        /// <param name="occurredAt">When it happened.</param>
        /// <param name="turnIndex">The turn it belongs to.</param>
        /// <param name="payload">The detail the fact carries.</param>
        internal void RaiseDiagnostic(
            ConversationEventKind kind,
            DateTimeOffset occurredAt,
            int? turnIndex,
            IReadOnlyDictionary<string, string>? payload = null)
        {
            Dispatch(kind, occurredAt, turnIndex, eventId: null, amends: null, payload);
        }

        /// <summary>Closes the chain of this conversation, once.</summary>
        /// <param name="reason">Why the conversation ended.</param>
        /// <param name="endedAt">The moment it ended.</param>
        /// <param name="terminalStage">The stage the machine stopped in, or <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when this conversation wrote the event, and <see langword="false"/> when it already had.</returns>
        internal bool EndConversation(ConversationEndReason reason, DateTimeOffset endedAt, string? terminalStage = null)
        {
            // The token is read first, so a value outside the closed set writes no event at all.
            string token = ConversationEndReasons.ToToken(reason);

            if (Interlocked.Exchange(ref _ended, 1) == 1)
            {
                return false;
            }

            Dictionary<string, string> payload = new(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.EndReason] = token,
            };

            if (terminalStage is { Length: > 0 })
            {
                payload[AuditPayloadKeys.StageAfter] = terminalStage;
            }

            _ = Raise(ConversationEventKind.ConversationEnded, endedAt, turnIndex: null, payload: payload);

            return true;
        }

        /// <summary>Raises the durable facts of one finished turn, in the order they happened.</summary>
        /// <param name="result">The turn that just spoke: its index, stages, the text the caller heard, when it ended, and how far it played.</param>
        /// <param name="generatedText">The words <c>turn.completed</c> proves.</param>
        /// <param name="heard">The words a cut turn's <c>reply.interrupted</c> proves.</param>
        /// <param name="toolFault">The message of the fault, or <see langword="null"/>.</param>
        /// <param name="played">How much of a cut reply played, or <see langword="null"/> when no voice layer knows it.</param>
        /// <returns>
        /// The identity of the <c>turn.completed</c> fact, so a barge-in that arrives after this turn
        /// already ended can name it through <see cref="ConversationEvent.AmendsEventId"/>.
        /// </returns>
        internal Guid WriteTurnEvents(TurnResult result, string generatedText, string heard, string? toolFault, TimeSpan? played)
        {
            (int turnIndex, DateTimeOffset endedAt) = (result.TurnIndex, result.EndedAt);

            if (toolFault is not null)
            {
                _ = Raise(
                    ConversationEventKind.ToolFailed,
                    endedAt,
                    turnIndex,
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AuditPayloadKeys.ToolError] = toolFault,
                    });
            }

            Guid completed = Raise(
                ConversationEventKind.TurnCompleted,
                endedAt,
                turnIndex,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(generatedText).Value,
                    [AuditPayloadKeys.StageBefore] = result.StageBefore,
                    [AuditPayloadKeys.StageAfter] = result.StageAfter,
                });

            if (result.Cut is not null)
            {
                RaiseReplyInterrupted(turnIndex, endedAt, completed, heard, played);
            }

            return completed;
        }

        /// <summary>Raises the amendment pair's second half: the barge-in that corrects one turn.</summary>
        /// <param name="turnIndex">The turn whose reply was cut.</param>
        /// <param name="occurredAt">When the cut was recorded.</param>
        /// <param name="amendsEventId">The identity of the <c>turn.completed</c> fact it corrects.</param>
        /// <param name="heard">The text the caller actually heard.</param>
        /// <param name="played">
        /// How much of the reply played, as the relay reported it, or <see langword="null"/> when nothing played it:
        /// the payload then omits the duration (design section 7, item 5).
        /// </param>
        internal void RaiseReplyInterrupted(
            int turnIndex,
            DateTimeOffset occurredAt,
            Guid amendsEventId,
            string heard,
            TimeSpan? played)
        {
            Dictionary<string, string> payload = new(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterruptSha256] = AuditHash.OfText(heard).Value,
            };

            if (played is { } duration)
            {
                payload[AuditPayloadKeys.DurationUntilInterruptMs] =
                    ((long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            _ = Raise(ConversationEventKind.ReplyInterrupted, occurredAt, turnIndex, amends: amendsEventId, payload: payload);
        }

        /// <summary>Raises the moderation facts of one turn, before the turn's own events.</summary>
        /// <param name="turnIndex">The turn that just ran.</param>
        /// <param name="disposition">What the layers reported, or <see langword="null"/>.</param>
        internal void RaiseModeration(int turnIndex, TurnDisposition? disposition)
        {
            switch (disposition?.Moderation)
            {
                case ModerationOutcome.Flagged:
                    _ = Raise(
                        ConversationEventKind.PromptFlagged,
                        _time.GetUtcNow(),
                        turnIndex,
                        payload: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [AuditPayloadKeys.ModerationCategories] = disposition.FlaggedCategories ?? string.Empty,
                        });
                    break;

                case ModerationOutcome.Unavailable:
                    RaiseDiagnostic(
                        ConversationEventKind.ModerationUnavailable,
                        _time.GetUtcNow(),
                        turnIndex,
                        payload: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [ConversationEventPayloadKeys.Reason] =
                                disposition.ModerationReason ?? ConversationSession.ModerationFaultedReason,
                        });
                    break;

                case ModerationOutcome.Clean:
                    RaiseDiagnostic(ConversationEventKind.ModerationClean, _time.GetUtcNow(), turnIndex);
                    break;

                default:
                    break;
            }
        }

        /// <summary>Raises the fact of one tool call that did not run to completion.</summary>
        /// <param name="turnIndex">The turn the conversation belongs to.</param>
        /// <param name="failure">What the function-invocation loop saw.</param>
        internal void RaiseToolFailure(int turnIndex, ToolFailure failure)
        {
            _ = Raise(
                        ConversationEventKind.ToolFailed,
                        _time.GetUtcNow(),
                        turnIndex,
                        payload: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [AuditPayloadKeys.ToolName] = failure.ToolName,
                            [AuditPayloadKeys.ToolCallId] = failure.ToolCallId,
                            [AuditPayloadKeys.ToolFailureKind] = ToolFailureKinds.ToToken(failure.Kind),
                            [AuditPayloadKeys.ToolError] = failure.Message,
                        });
        }

        /// <summary>Raises the fact of one store 1 write the backing store refused.</summary>
        internal void RaiseDroppedTranscriptWrite(int turnIndex, Exception exception)
        {
            RaiseDiagnostic(
                        ConversationEventKind.TranscriptWriteFailed,
                        _time.GetUtcNow(),
                        turnIndex,
                        payload: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [ConversationEventPayloadKeys.Reason] = $"{exception.GetType().Name}: {exception.Message}",
                        });
        }

        /// <summary>Raises the fact of one store 1 read that failed as a turn opened.</summary>
        internal void RaiseFailedTranscriptResync(int turnIndex, Exception exception)
        {
            RaiseDiagnostic(
                        ConversationEventKind.TranscriptResyncFailed,
                        _time.GetUtcNow(),
                        turnIndex,
                        payload: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [ConversationEventPayloadKeys.Reason] = $"{exception.GetType().Name}: {exception.Message}",
                        });
        }

        /// <summary>Hands one fact to everything watching the conversation, and never waits for it.</summary>
        /// <param name="kind">What happened.</param>
        /// <param name="occurredAt">When it happened.</param>
        /// <param name="turnIndex">The turn it belongs to, or <see langword="null"/>.</param>
        /// <param name="eventId">The identity it took, or <see langword="null"/> when it took none.</param>
        /// <param name="amends">The identity it corrects, or <see langword="null"/>.</param>
        /// <param name="payload">The detail it carries.</param>
        private void Dispatch(
            ConversationEventKind kind,
            DateTimeOffset occurredAt,
            int? turnIndex,
            Guid? eventId,
            Guid? amends,
            IReadOnlyDictionary<string, string>? payload)
        {
            _observers.Dispatch(new ConversationEvent
            {
                ConversationId = _conversationId,
                Kind = kind,
                OccurredAt = occurredAt,
                EventId = eventId,
                TurnIndex = turnIndex,
                AmendsEventId = amends,
                Payload = payload ?? new Dictionary<string, string>(StringComparer.Ordinal),
            });
        }
    }
}

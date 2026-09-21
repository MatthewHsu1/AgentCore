using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Audit
{
    /// <summary>
    /// Turns the durable facts of a call into the append-only chain of D23.
    /// </summary>
    internal sealed class AuditConversationObserver : IConversationObserver
    {
        private readonly IAuditSinkPort _sink;

        /// <summary>Creates the observer that writes the chain.</summary>
        /// <param name="sink">Where the rows go.</param>
        /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
        public AuditConversationObserver(IAuditSinkPort sink)
        {
            ArgumentNullException.ThrowIfNull(sink);

            _sink = sink;
        }

        /// <inheritdoc />
        public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(conversationEvent);

            // A diagnostic-only fact took no identity, so there is no row to write and nothing to report.
            if (conversationEvent.EventId is not { } eventId)
            {
                return ValueTask.CompletedTask;
            }

            // The two enums are not interchangeable, and a value outside the closed set is dropped rather
            // than thrown over: the turn loop pairs an identity with a stored kind, so this is unreachable
            // for any event the session raises, and audit is a record of the conversation and never a part of it.
            if (!ConversationEventKinds.TryGetAuditKind(conversationEvent.Kind, out AuditEventKind kind))
            {
                return ValueTask.CompletedTask;
            }

            AuditEvent auditEvent = new()
            {
                ConversationId = conversationEvent.ConversationId,
                EventId = eventId,
                Kind = kind,
                OccurredAt = conversationEvent.OccurredAt,
                TurnIndex = conversationEvent.TurnIndex,
                AmendsEventId = conversationEvent.AmendsEventId,
                Payload = conversationEvent.Payload,
            };

            // The token of the run belongs to the dispatcher, which passes CancellationToken.None: the
            // enqueue belongs to the record of the conversation, not to a turn the caller may have cancelled.
            return _sink.AppendAsync(auditEvent, cancellationToken);
        }
    }
}

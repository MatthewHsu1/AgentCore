using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Audit;

/// <summary>
/// Turns the durable facts of a call into the append-only chain of D23.
/// </summary>
internal sealed class AuditCallObserver : ICallObserver
{
    private readonly IAuditSinkPort _sink;

    /// <summary>Creates the observer that writes the chain.</summary>
    /// <param name="sink">Where the rows go.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public AuditCallObserver(IAuditSinkPort sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
    }

    /// <inheritdoc />
    public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callEvent);

        // A diagnostic-only fact took no identity, so there is no row to write and nothing to report.
        if (callEvent.EventId is not { } eventId)
        {
            return ValueTask.CompletedTask;
        }

        // The two enums are not interchangeable, and a value outside the closed set is dropped rather
        // than thrown over: the turn loop pairs an identity with a stored kind, so this is unreachable
        // for any event the session raises, and audit is a record of the call and never a part of it.
        if (!CallEventKinds.TryGetAuditKind(callEvent.Kind, out AuditEventKind kind))
        {
            return ValueTask.CompletedTask;
        }

        AuditEvent auditEvent = new()
        {
            CallId = callEvent.CallId,
            EventId = eventId,
            Kind = kind,
            OccurredAt = callEvent.OccurredAt,
            TurnIndex = callEvent.TurnIndex,
            AmendsEventId = callEvent.AmendsEventId,
            Payload = callEvent.Payload,
        };

        // The token of the run belongs to the dispatcher, which passes CancellationToken.None: the
        // enqueue belongs to the record of the call, not to a turn the caller may have cancelled.
        return _sink.AppendAsync(auditEvent, cancellationToken);
    }
}

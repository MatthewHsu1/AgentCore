using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Audit;

/// <summary>
/// Turns the durable facts of a call into the append-only chain of D23.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place that knows both vocabularies. <see cref="CallEvent"/> is neutral and carries
/// no wire token, no hash, and no sequence, and an <see cref="AuditEvent"/> is all three, so the
/// translation lives here rather than in the turn loop. <see cref="CallSession"/> no longer knows what
/// a sink is.
/// </para>
/// <para>
/// It writes six of the ten kinds. The other four are diagnostic only: they are counted and logged and
/// stored nowhere, and they carry no <see cref="CallEvent.Ordinal"/> precisely so that they consume no
/// number. Dropping them here is what keeps <see cref="AuditEvent.Sequence"/> gap-free and monotonic
/// from zero, exactly as it was before the hook existed.
/// </para>
/// <para>
/// The session allocates the ordinal, not this observer and not the sink, for the reason
/// <see cref="IAuditSinkPort"/> gives: the sink answers long after the turn moved on, so a number it
/// allocated would reach nobody in time, and a barge-in amendment has to name the turn event 91
/// nanoseconds later. <see cref="CallEvent.Ordinal"/> is copied straight into
/// <see cref="AuditEvent.Sequence"/>, and <see cref="CallEvent.AmendsOrdinal"/> into
/// <see cref="AuditEvent.AmendsSequence"/>. Nothing here renumbers anything.
/// </para>
/// <para>
/// It does not wait for the row. <see cref="IAuditSinkPort.AppendAsync"/> completes when the event is
/// ACCEPTED, and whatever it returns is returned to the dispatcher unchanged, so a queue costs the
/// caller the enqueue and a slow sink is observed off-turn. One instance serves every call.
/// </para>
/// </remarks>
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

        // A diagnostic-only fact took no number, so there is no row to write and nothing to report.
        if (callEvent.Ordinal is not { } ordinal)
        {
            return ValueTask.CompletedTask;
        }

        // The two enums are not interchangeable, and a value outside the closed set is dropped rather
        // than thrown over: the turn loop pairs an ordinal with a stored kind, so this is unreachable
        // for any event the session raises, and audit is a record of the call and never a part of it.
        if (!CallEventKinds.TryGetAuditKind(callEvent.Kind, out AuditEventKind kind))
        {
            return ValueTask.CompletedTask;
        }

        AuditEvent auditEvent = new()
        {
            CallId = callEvent.CallId,
            Sequence = ordinal,
            Kind = kind,
            OccurredAt = callEvent.OccurredAt,
            TurnIndex = callEvent.TurnIndex,
            AmendsSequence = callEvent.AmendsOrdinal,
            Payload = callEvent.Payload,
        };

        // The token of the run belongs to the dispatcher, which passes CancellationToken.None: the
        // enqueue belongs to the record of the call, not to a turn the caller may have cancelled.
        return _sink.AppendAsync(auditEvent, cancellationToken);
    }
}

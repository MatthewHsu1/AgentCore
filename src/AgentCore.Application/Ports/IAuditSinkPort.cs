using AgentCore.Domain.Audit;

namespace AgentCore.Application.Ports;

/// <summary>
/// Writes append-only, hash-chained audit events. It never sits on the turn.
/// </summary>
/// <remarks>
/// <para>
/// Section 7 names the adapter: <c>Infrastructure/Audit/Postgres</c>, behind a bounded
/// <c>Channel</c> and a background writer. D23 explains the shape. A durable insert costs 13 ms at
/// p50 and 32 ms at p99, against 91 nanoseconds to enqueue, so the turn loop must never wait for the
/// row. <see cref="AppendAsync"/> therefore completes when the event is ACCEPTED, not when it is
/// DURABLE, and an implementation that awaits the database inside it has broken the contract.
/// </para>
/// <para>
/// The consequence is that the caller never learns the hash of what it wrote. This is why an
/// amendment names <see cref="AuditEvent.Sequence"/> and not a hash: the hash of the turn event does
/// not exist yet when the barge-in amendment is enqueued. The caller allocates
/// <see cref="AuditEvent.Sequence"/> per call, and the adapter allocates the chain position and the
/// previous hash.
/// </para>
/// <para>
/// The port has one method, and it appends. There is no update, no delete, and no upsert, because
/// D23 enforces append-only in the engine as well: the writer role holds no <c>UPDATE</c>,
/// <c>DELETE</c>, or <c>TRUNCATE</c> grant, and a trigger refuses even the table owner. A correction
/// is a second event that names the first, per T23.
/// </para>
/// <para>
/// Verification is not on this port. <see cref="AuditChain.Verify"/> is pure and needs no adapter, so
/// <c>chain_check</c> reads the links out of any store and answers in Domain.
/// </para>
/// <para>
/// An implementation does not throw for a full queue, and it does not block. Audit is a record of the
/// call and never a part of it, so a sink that cannot keep up drops and reports rather than slowing
/// the caller down.
/// </para>
/// </remarks>
public interface IAuditSinkPort
{
    /// <summary>Accepts one event for the chain.</summary>
    /// <param name="auditEvent">The event to append. Nothing edits it afterwards.</param>
    /// <param name="cancellationToken">Cancels the enqueue, and never the write behind it.</param>
    /// <returns>
    /// A task that completes when the sink has accepted the event. The row is not durable yet.
    /// </returns>
    ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>Accepts a run of events for the chain, in the order they are given.</summary>
    /// <param name="auditEvents">The events to append. Nothing edits them afterwards.</param>
    /// <param name="cancellationToken">Cancels the enqueue, and never the writes behind it.</param>
    /// <returns>
    /// A task that completes when the sink has accepted every event. The rows are not durable yet.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="auditEvents"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>Overriding this is optional, and it is what a real store wants.</b> The default appends one
    /// at a time, so a sink that implements nothing but <see cref="AppendAsync"/> is complete. A store
    /// that pays per round trip overrides it and writes the run in one statement: section 7 measures a
    /// durable insert at 13 ms p50, and twenty of those cost 13 ms together and 260 ms apart.
    /// </para>
    /// <para>
    /// <see cref="Audit.QueuedAuditSink"/> is what calls this. Nothing in the turn loop does, because
    /// the turn loop raises one fact at a time; the run only exists once a queue has held events back
    /// while the store was busy.
    /// </para>
    /// <para>
    /// The order of <paramref name="auditEvents"/> is the order the call produced them, and an
    /// implementation keeps it. The chain of D23 is a record of a call, and a run written out of order
    /// is not one.
    /// </para>
    /// </remarks>
    ValueTask AppendManyAsync(
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvents);
        return AppendEachAsync(this, auditEvents, cancellationToken);

        static async ValueTask AppendEachAsync(
            IAuditSinkPort sink,
            IReadOnlyList<AuditEvent> auditEvents,
            CancellationToken cancellationToken)
        {
            foreach (AuditEvent auditEvent in auditEvents)
            {
                await sink.AppendAsync(auditEvent, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

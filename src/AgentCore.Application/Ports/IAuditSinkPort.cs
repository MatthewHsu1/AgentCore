using AgentCore.Domain.Audit;

namespace AgentCore.Application.Ports;

/// <summary>
/// Writes append-only, hash-chained audit events. It never sits on the turn.
/// </summary>
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

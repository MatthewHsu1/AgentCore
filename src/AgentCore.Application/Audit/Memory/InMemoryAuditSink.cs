using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Audit.Memory;

/// <summary>
/// The sink that keeps every event in a list.
/// </summary>
public sealed class InMemoryAuditSink : IAuditSinkPort
{
    private readonly Lock _gate = new();

    private readonly List<AuditEvent> _events = [];

    /// <summary>Gets the events this sink accepted, in the order they arrived.</summary>
    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>
    /// Reads back the events of one call, oldest first.
    /// </summary>
    /// <param name="callId">The id of the call.</param>
    /// <returns>The events of that call, in the order they arrived.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<AuditEvent> EventsOf(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_gate)
        {
            return [.. _events.Where(item => string.Equals(item.CallId, callId, StringComparison.Ordinal))];
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        AuditEventVocabulary.Validate(auditEvent);

        lock (_gate)
        {
            _events.Add(auditEvent);
        }

        return ValueTask.CompletedTask;
    }
}

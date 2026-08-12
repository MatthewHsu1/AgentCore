using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Audit;

/// <summary>
/// The sink that keeps every event in a list.
/// </summary>
/// <remarks>
/// <para>
/// This is the plainest implementation of <see cref="IAuditSinkPort"/> that the contract permits. It
/// accepts an event and returns, so nothing waits on it, which is what section 7 asks: a durable
/// insert costs 13 ms at p50 and 32 ms at p99, against 91 nanoseconds to enqueue.
/// </para>
/// <para>
/// <b>It is not the audit store.</b> D23 puts the chain in PostgreSQL, where the writer role holds no
/// <c>UPDATE</c> grant, a trigger refuses the table owner, and a <c>CHECK</c> constraint recomputes
/// SHA-256 inside the engine. A list in this process has none of those three defences. Use this to
/// read a chain back in a test, and to keep a host that has no database alive.
/// </para>
/// <para>
/// The list grows without a bound, so a long-running host replaces this sink rather than keeping it.
/// Events may arrive from several calls at once, so the append takes a lock.
/// </para>
/// </remarks>
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

    /// <summary>Reads back the events of one call, oldest first.</summary>
    /// <param name="callId">The id of the call.</param>
    /// <returns>The events of that call, in the order they arrived.</returns>
    /// <remarks>
    /// <see cref="AuditChain.LinkAll"/> turns the answer into a chain, and
    /// <see cref="AuditChain.Verify"/> is then the <c>chain_check</c> of section 11, item 6.
    /// </remarks>
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

        lock (_gate)
        {
            _events.Add(auditEvent);
        }

        return ValueTask.CompletedTask;
    }
}

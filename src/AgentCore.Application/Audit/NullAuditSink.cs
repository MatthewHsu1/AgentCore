using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Audit;

/// <summary>
/// The sink a session gets when the host bound none. It writes nowhere.
/// </summary>
/// <remarks>
/// The turn loop produces the events of D23 whether or not a store exists, so the default has to
/// accept them and drop them. A host that binds nothing therefore still answers a call, and a host
/// that binds the PostgreSQL sink of section 7 loses nothing by the default having been there.
/// </remarks>
internal sealed class NullAuditSink : IAuditSinkPort
{
    /// <summary>Gets the one instance. It holds no state, so one serves every call.</summary>
    public static NullAuditSink Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Ports;

/// <summary>
/// Opens the audit store behind one <c>providers.audit</c> value.
/// </summary>
public interface IAuditSinkAdapter : IVendorAdapter
{
    /// <summary>Opens the store this vendor writes to, and hands it over.</summary>
    /// <param name="entry">The <c>providers.audit</c> block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>
    /// The raw store. The caller wraps it in <see cref="Audit.QueuedAuditSink"/> and owns it for the
    /// life of the process, so this returns no queue and starts no background writer.
    /// </returns>
    ValueTask<IAuditSinkPort> OpenAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}

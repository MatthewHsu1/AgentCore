using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;

namespace AgentCore.Application.Audit;

/// <summary>
/// Opens the audit sink the document names, from the adapters the host registered.
/// </summary>
public static class AuditSinkFactory
{
    /// <summary>The built-in kind, and the one a document that names no provider gets.</summary>
    public const string MemoryKind = "memory";

    /// <summary>What this seam calls itself, so the shared selector writes its failures.</summary>
    private static readonly VendorSeam Seam =
        new("providers.audit", "/providers/audit/kind", "options.UseAuditSinks(...)", "sinks");

    /// <summary>Opens the sink <c>providers.audit</c> names, or the built-in one.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>
    /// The store, which is never <see langword="null"/>. It is the raw store and not a queue: the
    /// caller wraps it in <see cref="QueuedAuditSink"/>, which is what keeps the append off the turn.
    /// </returns>
    /// <exception cref="ArgumentNullException">The configuration or the adapters are <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationLoadException">
    /// The document names a <c>kind</c> no adapter serves, or a <c>kind</c> two adapters answer to.
    /// </exception>
    public static ValueTask<IAuditSinkPort> OpenAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        IReadOnlyList<IAuditSinkAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);

        // The document named nothing, or it named the built-in kind, and the two mean the same
        // thing: an absent block is read as kind: memory. Both are answered before the selector
        // runs, so neither needs a registered adapter and neither can be taken over by one.
        if (configuration.Providers?.Audit is not { } entry
            || string.Equals(entry.Kind, MemoryKind, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<IAuditSinkPort>(new InMemoryAuditSink());
        }

        var adapter = VendorAdapterSelector.Select(entry.Kind, adapters, Seam);
        return adapter.OpenAsync(entry, secrets, cancellationToken);
    }
}

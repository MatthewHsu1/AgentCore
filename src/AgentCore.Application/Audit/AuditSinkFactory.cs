using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;

namespace AgentCore.Application.Audit;

/// <summary>
/// Opens the audit sink the document names, from the adapters the host registered.
/// </summary>
/// <remarks>
/// <para>
/// This is the audit mirror of <c>TelemetrySessionFactory</c>, <c>ModerationEvaluatorFactory</c>, and
/// <c>CompositeKnowledgeStoreFactory</c>, and it takes the same shape: the host lists the vendors it
/// supports once, <c>providers.audit.kind</c> picks one, and a document that changes vendors changes
/// no code. A host that registered no adapter for a kind the document does name fails the start,
/// because that is a document asking for something this host cannot give.
/// </para>
/// <para>
/// <b>It never answers with nothing.</b> Telemetry has a null answer because a document that named no
/// collector asked for no export; audit has none, because the turn loop raises the D23 events whether
/// or not anybody chose where to put them, and the alternative to a sink is dropping them on the
/// floor. So a document that names no <c>providers.audit</c> gets <see cref="InMemoryAuditSink"/>. The
/// real chain wants a database, and a library that refused to start without one could not be used in
/// a test.
/// </para>
/// <para>
/// <b>The default is not the audit store.</b> <see cref="InMemoryAuditSink"/> keeps every event in a
/// list, which grows without a bound and holds none of the three defences D23 asks of PostgreSQL: a
/// writer role with no <c>UPDATE</c> grant, a trigger that refuses even the table owner, and a
/// <c>CHECK</c> constraint that recomputes SHA-256 inside the engine. A long-running deployment
/// therefore names a durable vendor, and leaves this default to tests and to hosts with no database.
/// </para>
/// <para>
/// <b><see cref="MemoryKind"/> is this library's own name, and a host cannot rebind it.</b> It is
/// answered here and short-circuits before the selector runs, so an adapter registered under
/// <c>memory</c> is never asked for anything. A host with a second in-process store gives it a name of
/// its own, which is also what keeps a document that writes <c>kind: memory</c> meaning one fixed
/// thing on every host that reads it.
/// </para>
/// </remarks>
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

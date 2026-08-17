using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Ports;

/// <summary>
/// Opens the audit store behind one <c>providers.audit</c> value.
/// </summary>
/// <remarks>
/// <para>
/// This is the audit mirror of <see cref="IChatClientAdapter"/>, <see cref="IKnowledgeStoreAdapter"/>,
/// <see cref="IModerationAdapter"/>, and <see cref="ITelemetryAdapter"/>, and it takes the same shape:
/// the host lists the vendors it supports once, <c>providers.audit.kind</c> picks one, and a document
/// that changes vendors changes no code.
/// </para>
/// <para>
/// D23 puts the chain in PostgreSQL, where the writer role holds no <c>UPDATE</c> grant, a trigger
/// refuses even the table owner, and a <c>CHECK</c> constraint recomputes SHA-256 inside the engine.
/// The adapter for that lives in <c>AgentCore.Infrastructure</c> with the driver behind it. Nothing in
/// this project knows PostgreSQL exists, so a deployment that keeps its chain somewhere else writes
/// one file and changes one line of its document.
/// </para>
/// <para>
/// <b>An adapter returns the raw store and never a queue.</b> The caller is what wraps the answer in
/// <see cref="Audit.QueuedAuditSink"/>, so a store that blocks on its database is a correct store, and
/// an adapter that carried a queue of its own would put two of them in a row. This is the same rule
/// <see cref="IAuditSinkPort"/> states for the port and the AspNetCore composition root applies when
/// it registers the sink; it is written here so a vendor author reads it in the file they implement.
/// </para>
/// <para>
/// <b>A document that names no <c>providers.audit</c> still gets a sink.</b> That is the one way this
/// seam differs from telemetry: the turn loop produces the D23 events regardless, so the default is
/// the in-process <see cref="Audit.InMemoryAuditSink"/> rather than nothing at all. See
/// <see cref="Audit.AuditSinkFactory"/>, which also owns the <c>memory</c> name and does not ask any
/// adapter for it.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// This runs once, while the host starts. A missing credential therefore stops the host and never
    /// a call, which is what item 9 of section 11 asks for, and it is the reason a connection is worth
    /// proving here rather than on the first event of the first call.
    /// </remarks>
    ValueTask<IAuditSinkPort> OpenAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}

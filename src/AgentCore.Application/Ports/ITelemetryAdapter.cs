using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Ports;

/// <summary>
/// Starts the telemetry export behind one <c>providers.telemetry</c> value.
/// </summary>
/// <remarks>
/// <para>
/// This is the telemetry mirror of <see cref="IChatClientAdapter"/>,
/// <see cref="IKnowledgeStoreAdapter"/>, and <see cref="IModerationAdapter"/>, and it takes the same
/// shape: the host lists the vendors it supports once, <c>providers.telemetry.kind</c> picks one, and
/// a document that changes vendors changes no code.
/// </para>
/// <para>
/// D26 sends every signal to Grafana Cloud over OTLP, and the adapter for it lives in
/// <c>AgentCore.Infrastructure</c> with the exporter package behind it. Nothing in this project knows
/// OTLP exists, so a deployment that exports somewhere else writes one file and changes one line of
/// its document.
/// </para>
/// <para>
/// <b>A document that names no <c>providers.telemetry</c> exports nothing.</b> Spans and
/// measurements are still written to the <c>ActivitySource</c> and <c>Meter</c> the library owns,
/// where they cost almost nothing with no listener attached, and log lines go wherever the host
/// already sends them. That is the deliberate default: telemetry needs a vendor account, and a
/// library that refused to start without one could not be used in a test.
/// </para>
/// </remarks>
public interface ITelemetryAdapter : IVendorAdapter
{
    /// <summary>Starts the export this vendor needs, and holds it.</summary>
    /// <param name="entry">The <c>providers.telemetry</c> block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running session. The host owns it for the life of the process.</returns>
    /// <remarks>
    /// This runs once, while the host starts. A missing credential therefore stops the host and never
    /// a call, which is what item 9 of section 11 asks for. It opens no socket: an exporter connects
    /// when it first has something to send, so a host with no route to the collector still starts.
    /// </remarks>
    ValueTask<ITelemetrySession> StartAsync(
        TelemetryProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}

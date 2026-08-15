using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;

namespace AgentCore.Application.Diagnostics;

/// <summary>
/// Starts the telemetry session the document names, from the adapters the host registered.
/// </summary>
/// <remarks>
/// <para>
/// This is the telemetry mirror of <c>ModerationEvaluatorFactory</c> and
/// <c>CompositeKnowledgeStoreFactory</c>, and it takes the same shape: the host lists the vendors it
/// supports once, <c>providers.telemetry.kind</c> picks one, and a document that changes vendors
/// changes no code.
/// </para>
/// <para>
/// A document that names no <c>providers.telemetry</c> gets no session, and a host that registered no
/// adapter for a kind the document does name fails the start. Both are deliberate: the first is a
/// document that asked for nothing, and the second is a document that asked for something this host
/// cannot give.
/// </para>
/// </remarks>
public static class TelemetrySessionFactory
{
    /// <summary>What this seam calls itself, so the shared selector writes its failures.</summary>
    private static readonly VendorSeam Seam =
        new("providers.telemetry", "/providers/telemetry/kind", "options.UseTelemetry(...)", "collectors");

    /// <summary>Starts the session <c>providers.telemetry</c> names, or none.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>
    /// The running session, or <see langword="null"/> when the document names no telemetry provider.
    /// The caller owns it and must dispose it, because disposal is what flushes.
    /// </returns>
    /// <exception cref="ArgumentNullException">The configuration or the adapters are <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationLoadException">
    /// The document names a <c>kind</c> no adapter serves, or a <c>kind</c> two adapters answer to.
    /// </exception>
    public static async ValueTask<ITelemetrySession?> StartAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        IReadOnlyList<ITelemetryAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);

        if (configuration.Providers?.Telemetry is not { } entry)
        {
            // The document asked for nothing, so no adapter is asked to start anything and no
            // credential is read. A host that registers a vendor still starts with no key.
            return null;
        }

        var adapter = VendorAdapterSelector.Select(entry.Kind, adapters, Seam);
        return await adapter.StartAsync(entry, secrets, cancellationToken).ConfigureAwait(false);
    }
}

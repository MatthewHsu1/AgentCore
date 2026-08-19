using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using AgentCore.Application.Transcript.Memory;

namespace AgentCore.Application.Transcript;

/// <summary>
/// Opens the store 1 backing the document names, from the adapters the host registered.
/// </summary>
public static class TranscriptStoreFactory
{
    /// <summary>The built-in kind, and the one a document that names no provider gets.</summary>
    public const string MemoryKind = "memory";

    /// <summary>What this seam calls itself, so the shared selector writes its failures.</summary>
    private static readonly VendorSeam Seam =
        new("providers.transcript", "/providers/transcript/kind", "options.UseTranscriptStores(...)", "stores");

    /// <summary>Opens the store <c>providers.transcript</c> names, or the built-in one.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The store, which is never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">The configuration or the adapters are <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationLoadException">
    /// The document names a <c>kind</c> no adapter serves, or a <c>kind</c> two adapters answer to.
    /// </exception>
    public static ValueTask<ITranscriptStore> OpenAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        IReadOnlyList<ITranscriptStoreAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);

        if (configuration.Providers?.Transcript is not { } entry
            || string.Equals(entry.Kind, MemoryKind, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<ITranscriptStore>(new InMemoryTranscriptStore());
        }

        var adapter = VendorAdapterSelector.Select(entry.Kind, adapters, Seam);
        return adapter.OpenAsync(entry, secrets, cancellationToken);
    }
}

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Embeddings;

/// <summary>
/// The composite a config-driven host builds its embedding generator through. It routes
/// <c>providers.embeddings.kind</c> to the <see cref="IEmbeddingGeneratorAdapter"/> whose kind
/// matches.
/// </summary>
public static class CompositeEmbeddingGeneratorFactory
{
    /// <summary>What the embeddings field calls itself, so the shared selector writes its failures.</summary>
    private static readonly VendorSeam EmbeddingSeam =
        new("providers.embeddings.kind", "/providers/embeddings/kind", "options.UseEmbeddings(...)", "generators");

    /// <summary>Builds the generator the document names, or nothing when it names none.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The chain the adapter resolves its credential through, or <see langword="null"/>.</param>
    /// <param name="adapters">The adapters the host registers, one for each vendor it supports.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The generator, or <see langword="null"/> when the document has no <c>providers.embeddings</c> block.</returns>
    /// <exception cref="Configuration.Parsing.ConfigurationLoadException">
    /// <c>providers.embeddings.kind</c> names a kind no adapter serves, or a kind two adapters
    /// answer to.
    /// </exception>
    public static async ValueTask<IEmbeddingGenerator<string, Embedding<float>>?> CreateAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        IReadOnlyList<IEmbeddingGeneratorAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);

        if (configuration.Providers?.Embeddings is not { } entry)
        {
            return null;
        }

        var adapter = VendorAdapterSelector.Select(entry.Kind, adapters, EmbeddingSeam);

        return await adapter.CreateGeneratorAsync(entry, secrets, cancellationToken).ConfigureAwait(false);
    }
}

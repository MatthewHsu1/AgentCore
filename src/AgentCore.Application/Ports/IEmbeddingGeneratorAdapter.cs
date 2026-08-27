using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// Builds the embedding generator behind one <c>providers.embeddings.kind</c> value.
/// </summary>
public interface IEmbeddingGeneratorAdapter : IVendorAdapter
{
    /// <summary>Builds the vendor generator.</summary>
    /// <param name="entry">The <c>providers.embeddings</c> block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateGeneratorAsync(
        EmbeddingProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}

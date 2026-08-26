using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Embeddings;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Step 3a: build the embedding generator the document names, before knowledge opens.</summary>
internal static class EmbeddingStartup
{
    /// <summary>Builds the generator the document names.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.embeddings</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors.</param>
    /// <param name="cancellationToken">Cancels the adapter build.</param>
    internal static ValueTask<IEmbeddingGenerator<string, Embedding<float>>?> OpenAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CancellationToken cancellationToken)
        => options.Embeddings is { } adapters
            ? CompositeEmbeddingGeneratorFactory.CreateAsync(
                configuration, options.SecretResolver, adapters, cancellationToken)
            : ValueTask.FromResult<IEmbeddingGenerator<string, Embedding<float>>?>(null);
}

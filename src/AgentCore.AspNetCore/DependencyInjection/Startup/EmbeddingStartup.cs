using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Embeddings;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>Step 3a: build the embedding generator the document names, before knowledge opens.</summary>
    internal static class EmbeddingStartup
    {
        /// <summary>Builds the generator the document names.</summary>
        /// <param name="configuration">The loaded document. It carries <c>providers.embeddings</c>.</param>
        /// <param name="options">The options the host filled. It carries the registered vendors.</param>
        /// <param name="cancellationToken">Cancels the adapter build.</param>
        internal static async ValueTask<IEmbeddingGenerator<string, Embedding<float>>?> OpenAsync(
            AgentCoreConfiguration configuration,
            AgentCoreOptions options,
            CancellationToken cancellationToken)
        {
            if (options.Embeddings is not { } adapters)
            {
                return null;
            }

            IEmbeddingGenerator<string, Embedding<float>>? generator = await CompositeEmbeddingGeneratorFactory
                .CreateAsync(configuration, options.SecretResolver, adapters, cancellationToken)
                .ConfigureAwait(false);

            return generator is null || options.Cache is not { } cache
                ? generator
                : new HybridCachingEmbeddingGenerator(generator, cache);
        }
    }
}

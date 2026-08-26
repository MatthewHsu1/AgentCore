using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// Builds the knowledge port behind one <c>providers.knowledge.kind</c> value.
/// </summary>
public interface IKnowledgeStoreAdapter : IVendorAdapter
{
    /// <summary>Gets whether this adapter answers <see cref="IKnowledgeRetrievalPort"/>.</summary>
    bool CanServeSearch { get; }

    /// <summary>Gets whether this adapter can confine a search to a <c>KnowledgeScope</c>.</summary>
    bool CanScope { get; }

    /// <summary>Builds the knowledge base.</summary>
    /// <param name="entry">The <c>providers.knowledge</c> block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="embeddings">
    /// The generator <c>providers.embeddings</c> built, or <see langword="null"/> when the document
    /// names none. An adapter that ranks by vector fails the build on <see langword="null"/>, by
    /// name; an adapter that ranks without vectors ignores it.
    /// </param>
    /// <param name="requireScope">
    /// Whether every agent reading this store declares <c>scoped: true</c>. The store is shared
    /// across every agent that reads it, so an adapter that fails closed with no ambient
    /// <c>KnowledgeScope</c> open is only correct when ALL of them want scoping -- one store cannot
    /// enforce a scope for one agent while staying open for another. A mixed deployment passes
    /// <see langword="false"/> here, and the per-agent gate then lives in the caller instead of the
    /// store.
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The port. The host owns it for the life of the process.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanServeSearch"/> is <see langword="false"/>.</exception>
    ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        bool requireScope,
        CancellationToken cancellationToken = default);
}

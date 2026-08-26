using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Step 3b: open the knowledge base the document names, before any tool is built.</summary>
internal static class KnowledgeStartup
{
    /// <summary>Opens the knowledge port the document names.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.knowledge</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors and any explicit seam.</param>
    /// <param name="startup">The loaded document and the resolved secrets.</param>
    /// <param name="embeddings">
    /// The generator <c>providers.embeddings</c> built, or <see langword="null"/> when the document
    /// names none. Handed to the matched adapter, which fails the start by name when it ranks by
    /// vector and received none.
    /// </param>
    /// <param name="scopeDeclared">Whether ANY agent in the document declares <c>knowledge: { scoped: true }</c>.</param>
    /// <param name="requireScope">
    /// Whether EVERY agent in the document declares <c>knowledge: { scoped: true }</c>. See
    /// <see cref="CompositeKnowledgeStoreFactory.CreateAsync"/> for why this is a different question
    /// from <paramref name="scopeDeclared"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the adapter build.</param>
    /// <returns>The port, open, or <see langword="null"/>.</returns>
    internal static ValueTask<IKnowledgeRetrievalPort?> OpenAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        AgentCoreStartup startup,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        bool scopeDeclared,
        bool requireScope,
        CancellationToken cancellationToken)
    {
        if (options.KnowledgeRetrieval is { } retrieval)
        {
            return ValueTask.FromResult<IKnowledgeRetrievalPort?>(retrieval(startup));
        }

        if (configuration.Providers?.Knowledge is null && !AnyAgentDeclares(configuration))
        {
            return ValueTask.FromResult<IKnowledgeRetrievalPort?>(null);
        }

        return options.KnowledgeStores is { } stores
            ? CompositeKnowledgeStoreFactory.CreateAsync(
                configuration, options.SecretResolver, stores, embeddings, scopeDeclared, requireScope, cancellationToken)
            : ValueTask.FromResult<IKnowledgeRetrievalPort?>(null);
    }

    /// <summary>Whether any agent's <c>knowledge:</c> block composes.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <returns><see langword="true"/> when at least one agent reads the knowledge base.</returns>
    private static bool AnyAgentDeclares(AgentCoreConfiguration configuration)
        => configuration.Agents is { } agents && AgentKnowledge.AnyDeclared(agents);
}

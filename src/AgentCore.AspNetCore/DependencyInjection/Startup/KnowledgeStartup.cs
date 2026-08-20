using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Step 3b: open the knowledge base the document names, before any tool is built.</summary>
internal static class KnowledgeStartup
{
    /// <summary>Opens the two knowledge ports the document names.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.knowledge</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors and any explicit seam.</param>
    /// <param name="cancellationToken">Cancels the adapter builds.</param>
    /// <returns>The two ports, each one open or <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// The host listed its knowledge vendors once and <c>providers.knowledge</c> picks one for each
    /// port, so both ports are open before any tool is built. A host that registered no vendor keeps
    /// two null ports and binds whatever its explicit seams hold.
    /// </para>
    /// <para>
    /// A port an explicit seam already sets is not asked for here at all. The registry then reads
    /// neither that field's kind nor its vendor, so an override costs no vendor build and a kind this
    /// host does not register cannot fail a start that was never going to use it.
    /// </para>
    /// </remarks>
    internal static ValueTask<(IKnowledgeRetrievalPort? Search, IDocumentStorePort? Documents)> OpenAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CancellationToken cancellationToken)
        => options.KnowledgeStores is { } stores
            ? CompositeKnowledgeStoreFactory.CreateAsync(
                configuration,
                options.SecretResolver,
                stores,
                includeSearch: options.KnowledgeRetrieval is null,
                includeDocuments: options.DocumentStore is null,
                cancellationToken)
            : ValueTask.FromResult<(IKnowledgeRetrievalPort?, IDocumentStorePort?)>(default);
}

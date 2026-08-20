using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Step 4: build the one tool factory chain the compile table asks.</summary>
/// <remarks>
/// A link answers null for a kind it does not serve, and the composite fails the start when no link
/// serves a kind the document declares.
/// </remarks>
internal static class ToolFactoryStartup
{
    /// <summary>Builds the one tool factory the compile table asks.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <param name="startup">The loaded document and the resolved secrets.</param>
    /// <param name="knowledge">The two ports the knowledge registry opened, each one or <see langword="null"/>.</param>
    /// <returns>The composite, over every link the host bound.</returns>
    internal static CompositeAgentToolFactory Build(
        AgentCoreOptions options,
        AgentCoreStartup startup,
        (IKnowledgeRetrievalPort? Search, IDocumentStorePort? Documents) knowledge)
    {
        List<IAgentToolFactory> links = [];

        // An explicit UseKnowledgeRetrieval, UseDocumentStore, or UseKnowledge call wins over the
        // UseKnowledgeStores registry, for the port it sets. A host that wants one half of its own
        // and one half from the document writes both calls, and neither one hides the other. Step 3b
        // already left the shadowed half unresolved and unbuilt, so the half it did open is the only
        // one this reads.
        var retrieval = options.KnowledgeRetrieval is { } bound ? bound(startup) : knowledge.Search;
        var documents = options.DocumentStore is { } boundDocuments ? boundDocuments(startup) : knowledge.Documents;

        // The two knowledge ports bind apart, so one of the two is enough for the link to be worth
        // adding. The link then serves the built-in whose port is bound and fails the load on the
        // built-in whose port is not.
        if (retrieval is not null || documents is not null)
        {
            links.Add(new BuiltinToolFactory(retrieval, documents));
        }

        // The binding link needs no adapter. The registry is the seam the host already filled.
        links.Add(new BindingToolFactory(options.Bindings));

        foreach (var extra in options.ToolFactories)
        {
            links.Add(extra(startup));
        }

        return new CompositeAgentToolFactory(links);
    }
}

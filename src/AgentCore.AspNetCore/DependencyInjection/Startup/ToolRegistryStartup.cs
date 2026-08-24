using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Infrastructure.Tools;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Everything step 4 built: the registry, and the MCP source it must own, if any.</summary>
/// <param name="Registry">The registry the compile table reads. Its own <c>Ids</c> are every id it serves.</param>
/// <param name="McpSource">
/// The source that holds every MCP client this step opened, or <see langword="null"/> when the
/// document declared no <c>mcp:</c> server. It is <see cref="IAsyncDisposable"/>, and closing the
/// stdio child processes and HTTP sessions behind it is the composition root's job: nothing else
/// opened them.
/// </param>
internal readonly record struct ToolRegistryBuildResult(ToolRegistry Registry, McpToolSource? McpSource);

/// <summary>Step 4: build the one tool registry the compile table reads.</summary>
internal static class ToolRegistryStartup
{
    /// <summary>Asks every source what it serves, and holds the answers.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <param name="startup">The loaded document and the resolved secrets.</param>
    /// <param name="knowledge">The two ports the knowledge registry opened, each one or <see langword="null"/>.</param>
    /// <param name="chatClients">The factory a shipped agent runs on. Step 3c fails the boot rather than returning none.</param>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="cancellationToken">Cancels the discovery.</param>
    /// <returns>The registry, and the MCP source the composition root must own, if one was built.</returns>
    internal static async ValueTask<ToolRegistryBuildResult> BuildAsync(
        AgentCoreOptions options,
        AgentCoreStartup startup,
        (IKnowledgeRetrievalPort? Search, IDocumentStorePort? Documents) knowledge,
        IChatClientFactory chatClients,
        AgentCoreConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // An explicit UseKnowledgeRetrieval, UseDocumentStore, or UseKnowledge call wins over the
        // UseKnowledgeStores registry, for the port it sets. A host that wants one half of its own
        // and one half from the document writes both calls, and neither one hides the other. Step 3b
        // already left the shadowed half unresolved and unbuilt, so the half it did open is the only
        // one this reads.
        var retrieval = options.KnowledgeRetrieval is { } bound ? bound(startup) : knowledge.Search;
        var documents = options.DocumentStore is { } boundDocuments ? boundDocuments(startup) : knowledge.Documents;

        List<IToolSource> sources =
        [
            new BuiltinToolSource(new BuiltinToolPorts(retrieval, documents, chatClients)),
            new BindingToolSource(options.Bindings),
        ];

        foreach (var extra in options.ToolSources)
        {
            sources.Add(extra(startup));
        }

        // A source that connects to nothing still costs a boot step, so this is built only when the
        // document actually declares a server.
        McpToolSource? mcp = null;
        if (configuration.Mcp.Count > 0)
        {
            mcp = new McpToolSource();
            sources.Add(mcp);
        }

        var registry = await ToolRegistryBuilder
            .BuildAsync(sources, new ToolSourceContext(configuration), cancellationToken)
            .ConfigureAwait(false);

        return new ToolRegistryBuildResult(registry, mcp);
    }
}

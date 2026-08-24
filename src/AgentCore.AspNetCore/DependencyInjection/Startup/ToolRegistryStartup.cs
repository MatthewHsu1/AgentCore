using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Everything step 4 built: the registry, and every source it must close at shutdown.</summary>
/// <param name="Registry">The registry the compile table reads. Its own <c>Ids</c> are every id it serves.</param>
/// <param name="ServedIds">
/// Every id decision 15's reference pass may treat as satisfied: <paramref name="Registry"/>'s own
/// <c>Ids</c>, unioned with every declared <c>kind: agent</c> tool id. That kind reaches no source —
/// the compile table builds it once the agent it names has compiled — so <see cref="ToolRegistry"/>
/// never holds it, the same reason <see cref="ToolRegistryBuilder.BuildAsync"/> carves it out of its
/// own "every declaration is served" check. Computed once, here, so the composition root's reference
/// pass and that check can never silently disagree about which ids count as served.
/// </param>
/// <param name="Owned">
/// Every source among <c>options.ToolSources</c> — <c>AddToolSource</c>'s own doc comment covers
/// <c>McpToolSource</c>, registered this way by <c>AgentCore.Hosting</c> — that implements
/// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>. Disposal happens once, at host stop,
/// when the resource is going away regardless, so the risk of closing something a host still wants is
/// small even though <c>AddToolSource</c>'s factory could in principle be called more than once by a
/// host that keeps its own reference to what it returns.
/// </param>
internal readonly record struct ToolRegistryBuildResult(
    ToolRegistry Registry, IReadOnlySet<string> ServedIds, IReadOnlyList<IToolSource> Owned);

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
    /// <returns>The registry, the served-ids union decision 15's reference pass reads, and every source among them the composition root must own.</returns>
    /// <exception cref="AgentCore.Application.Configuration.Parsing.ConfigurationLoadException">
    /// A source's own discovery fails, an id collision is found, or the document declares a tool no
    /// source serves. Every source already open by then is closed before this rethrows, so a boot
    /// that fails here still leaves nothing running behind it.
    /// </exception>
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

        ToolRegistry registry;
        try
        {
            registry = await ToolRegistryBuilder
                .BuildAsync(sources, new ToolSourceContext(configuration), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // A source can open a client (or a child process) before the builder ever throws — an id
            // collision or an undeclared tool is only found once every source has already answered.
            // Each dispose is guarded on its own, so one source's failure to close cannot abandon the
            // rest, or replace the exception a deployer actually needs to read.
            foreach (var source in sources)
            {
                try
                {
                    switch (source)
                    {
                        case IAsyncDisposable asyncDisposable:
                            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                            break;
                        case IDisposable disposable:
                            disposable.Dispose();
                            break;
                    }
                }
                catch
                {
                    // The original failure is the one a deployer needs; a source that also fails to
                    // close must not replace or hide it.
                }
            }

            throw;
        }

        var servedIds = registry.Ids.ToHashSet(StringComparer.Ordinal);
        foreach (var tool in configuration.Tools)
        {
            if (tool.Kind == ToolKind.Agent)
            {
                servedIds.Add(tool.Id);
            }
        }

        List<IToolSource> owned = [.. sources.Where(source => source is IAsyncDisposable or IDisposable)];

        return new ToolRegistryBuildResult(registry, servedIds, owned);
    }
}

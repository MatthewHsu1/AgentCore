using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Registry;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>Everything step 4 built.</summary>
    /// <param name="Registry">The registry the compile table reads. Its own <c>Ids</c> are every id it serves.</param>
    /// <param name="ServedIds">
    /// Every id decision 15's reference pass may treat as satisfied: <paramref name="Registry"/>'s own
    /// <c>Ids</c>, unioned with every declared <c>kind: agent</c> tool id. That kind reaches no source —
    /// the compile table builds it once the agent it names has compiled — so <see cref="ToolRegistry"/>
    /// never holds it, the same reason <see cref="ToolRegistryBuilder.BuildAsync"/> carves it out of its
    /// own "every declaration is served" check. Computed once, here, so the composition root's reference
    /// pass and that check can never silently disagree about which ids count as served.
    /// </param>
    internal readonly record struct ToolRegistryBuildResult(
        ToolRegistry Registry, IReadOnlySet<string> ServedIds);

    /// <summary>Step 4: build the one tool registry the compile table reads.</summary>
    internal static class ToolRegistryStartup
    {
        /// <summary>Asks every source what it serves, and holds the answers.</summary>
        /// <param name="boot">The owner every source is tracked against, the moment it is built.</param>
        /// <param name="options">The options the host filled.</param>
        /// <param name="startup">The loaded document and the resolved secrets.</param>
        /// <param name="ports">
        /// What a shipped built-in runs on: the chat client factory step 3c built (it fails the boot rather
        /// than returning none), the blob store step 4d opened or <see langword="null"/>, the workspace
        /// root, and the logger factory.
        /// </param>
        /// <param name="cancellationToken">Cancels the discovery.</param>
        /// <returns>The registry, and the served-ids union decision 15's reference pass reads.</returns>
        /// <exception cref="Application.Configuration.Parsing.ConfigurationLoadException">
        /// A source's own discovery fails, an id collision is found, or the document declares a tool no
        /// source serves. Every source is tracked against <paramref name="boot"/> before discovery runs,
        /// so a source that opened a client — or a child process — is closed however this fails.
        /// </exception>
        internal static async ValueTask<ToolRegistryBuildResult> BuildAsync(
            AgentCoreBoot boot,
            AgentCoreOptions options,
            AgentCoreStartup startup,
            BuiltinToolPorts ports,
            CancellationToken cancellationToken)
        {
            AgentCoreConfiguration configuration = startup.Configuration;

            List<IToolSource> sources =
            [
                new BuiltinToolSource(ports),
                new BindingToolSource(options.Bindings),
            ];

            foreach (Func<AgentCoreStartup, IToolSource> extra in options.ToolSources)
            {
                sources.Add(boot.Track(extra(startup)));
            }

            if (configuration.Mcp.Count > 0 && options.ToolSources.Count == 0)
            {
                throw ToolSourceError.Fail(
                    "the document declares mcp:, and nothing registered a tool source to connect to it. Conversation "
                    + "AddAgentCoreHost (AgentCore.Hosting), or register one yourself with "
                    + "options.AddToolSource(...).");
            }

            ToolRegistry registry = await ToolRegistryBuilder
                .BuildAsync(sources, new ToolSourceContext(configuration) { Cache = options.Cache }, cancellationToken)
                .ConfigureAwait(false);

            HashSet<string> servedIds = registry.Ids.ToHashSet(StringComparer.Ordinal);

            foreach (ToolConfiguration tool in configuration.Tools)
            {
                if (tool.Kind != ToolKind.Agent)
                {
                    continue;
                }

                if (registry.Ids.Contains(tool.Id))
                {
                    throw ToolSourceError.Fail(
                        $"two tools claim the id '{tool.Id}': a discovered tool serves it, and the "
                        + "document's own kind: agent declaration claims it too. An id names one tool, so "
                        + "rename one of them.");
                }

                _ = servedIds.Add(tool.Id);
            }

            return new ToolRegistryBuildResult(registry, servedIds);
        }
    }
}

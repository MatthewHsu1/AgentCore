using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>
    /// An agent used as a node of any graph may not declare <c>background:</c>.
    /// </summary>
    internal static class GraphNodeBackgroundCheck
    {
        public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
        {
            Dictionary<string, string> nodeEntry = NodeAgentEntries(configuration);
            IReadOnlyList<AgentConfiguration> items = configuration.Agents.Items;

            for (int index = 0; index < items.Count; index++)
            {
                AgentConfiguration agent = items[index];
                if (agent.Background.Count > 0 && nodeEntry.TryGetValue(agent.Id, out string? entryName))
                {
                    errors.Add(GraphNodeBackground(
                        ConfigurationError.AppendPointer(ValidationPointer.Agent(index), "background"),
                        $"the agent '{agent.Id}' declares background: and is also used as a node in the graph "
                        + $"of entry '{entryName}'. MAF has no way to release a graph node's session, so a "
                        + "background child started inside a node would keep running after the conversation "
                        + "ends. Remove background: from this agent, or stop using it as a graph node."));
                }
            }
        }

        /// <summary>The first entry name that binds each agent id as a graph node, pattern or explicit alike.</summary>
        private static Dictionary<string, string> NodeAgentEntries(AgentCoreConfiguration configuration)
        {
            Dictionary<string, string> nodeEntry = new(StringComparer.Ordinal);

            foreach ((string? name, EntryConfiguration? entry) in configuration.Entries)
            {
                if (entry.Graph is not { } graph)
                {
                    continue;
                }

                foreach (string agentId in graph.Agents)
                {
                    _ = nodeEntry.TryAdd(agentId, name);
                }

                foreach (GraphNodeConfiguration node in graph.Nodes)
                {
                    if (node.Agent is { } agentId)
                    {
                        _ = nodeEntry.TryAdd(agentId, name);
                    }
                }
            }

            return nodeEntry;
        }
    }
}

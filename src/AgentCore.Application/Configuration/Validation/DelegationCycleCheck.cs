using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>Check 8: no agent reaches itself through a chain of <c>kind: agent</c> tools.</summary>
    internal static class DelegationCycleCheck
    {
        private sealed record Edge(string Target, int Agent, int Slot);

        public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
        {
            IReadOnlyList<AgentConfiguration> items = configuration.Agents.Items;
            if (items.Count == 0)
            {
                return;
            }

            Dictionary<string, List<Edge>> edges = BuildEdges(configuration);
            Dictionary<string, int> state = new(StringComparer.Ordinal);
            List<string> path = [];
            HashSet<string> reported = new(StringComparer.Ordinal);

            foreach (AgentConfiguration agent in items)
            {
                Visit(agent.Id, edges, state, path, reported, errors);
            }
        }

        // One edge for each agent-as-tool: the agent lists a tool, and that tool declares kind: agent.
        // The 'agent:' field is the one explicit delegation edge in the document. 'uses:' names a
        // built-in and 'binds:' names a host delegate, so neither ever names an agent, and a tool id
        // that matches an agent id is a coincidence.
        private static Dictionary<string, List<Edge>> BuildEdges(AgentCoreConfiguration configuration)
        {
            Dictionary<string, ToolConfiguration> tools = configuration.Tools.ToDictionary(static tool => tool.Id, static tool => tool, StringComparer.Ordinal);
            Dictionary<string, List<Edge>> edges = new(StringComparer.Ordinal);
            IReadOnlyList<AgentConfiguration> items = configuration.Agents.Items;

            for (int index = 0; index < items.Count; index++)
            {
                AgentConfiguration agent = items[index];
                List<Edge> outgoing = [];

                for (int slot = 0; slot < agent.Tools.Count; slot++)
                {
                    if (tools.TryGetValue(agent.Tools[slot], out ToolConfiguration? tool)
                        && tool.Kind == ToolKind.Agent
                        && tool.Agent is { } target)
                    {
                        outgoing.Add(new Edge(target, index, slot));
                    }
                }

                edges[agent.Id] = outgoing;
            }

            return edges;
        }

        private static void Visit(
            string agent,
            Dictionary<string, List<Edge>> edges,
            Dictionary<string, int> state,
            List<string> path,
            HashSet<string> reported,
            List<ConfigurationError> errors)
        {
            if (state.TryGetValue(agent, out int mark) && mark != 0)
            {
                return;
            }

            state[agent] = 1;
            path.Add(agent);

            List<Edge> outgoing = edges.TryGetValue(agent, out List<Edge>? found) ? found : [];
            foreach (Edge edge in outgoing)
            {
                int start = path.IndexOf(edge.Target);
                if (start < 0)
                {
                    Visit(edge.Target, edges, state, path, reported, errors);
                    continue;
                }

                string cycle = string.Join(" -> ", path.Skip(start).Append(edge.Target));
                if (reported.Add(cycle))
                {
                    errors.Add(CycleError(edge, cycle));
                }
            }

            path.RemoveAt(path.Count - 1);
            state[agent] = 2;
        }

        private static ConfigurationError CycleError(Edge edge, string cycle)
        {
            return new()
            {
                Pointer = ValidationPointer.AgentTool(edge.Agent, edge.Slot),
                // The compiler prints the same chain in the same 'first -> second -> first' form, and
                // gives the same reason. The two messages agree rather than compete.
                Message = $"this tool runs the agent '{edge.Target}', and that closes the delegation cycle "
                                  + $"{cycle}. The conversation would never return.",
                Check = ConfigurationCheck.DelegationCycles,
            };
        }
    }
}

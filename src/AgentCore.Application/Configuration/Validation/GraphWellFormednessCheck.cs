using System.Globalization;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>Check 7: one start node, no orphan, and a path from every node to an output.</summary>
    internal static class GraphWellFormednessCheck
    {
        public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
        {
            foreach ((string? name, EntryConfiguration? entry) in configuration.Entries)
            {
                if (entry.Graph is { } graph && graph.Nodes.Count > 0)
                {
                    CheckGraph(name, graph, errors);
                }
            }
        }

        private static void CheckGraph(string name, GraphConfiguration graph, List<ConfigurationError> errors)
        {
            int starts = graph.Nodes.Count(static node => node.Start);
            if (starts != 1)
            {
                errors.Add(WellFormedness(
                    ValidationPointer.GraphNodes(name),
                    string.Create(CultureInfo.InvariantCulture, $"the graph in entry '{name}' declares {starts} start nodes, and check 7 needs exactly one")));
            }

            CheckOrphans(name, graph, errors);

            HashSet<string> outputs = graph.Nodes.Where(static node => node.Output).Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
            if (outputs.Count == 0)
            {
                errors.Add(WellFormedness(ValidationPointer.GraphNodes(name), "the graph declares no output node, so no path reaches an output"));
                return;
            }

            Dictionary<string, List<string>> forward = GraphReach.BuildAdjacency(graph);
            for (int index = 0; index < graph.Nodes.Count; index++)
            {
                GraphNodeConfiguration node = graph.Nodes[index];
                if (!GraphReach.Reach(node.Id, forward).Overlaps(outputs))
                {
                    errors.Add(WellFormedness(
                        ValidationPointer.Node(name, index),
                        $"no path from the node '{node.Id}' reaches an output node"));
                }
            }
        }

        private static void CheckOrphans(string name, GraphConfiguration graph, List<ConfigurationError> errors)
        {
            HashSet<string> connected = new(StringComparer.Ordinal);
            foreach (GraphEdgeConfiguration edge in graph.Edges)
            {
                _ = connected.Add(edge.From);
                _ = connected.Add(edge.To);
            }

            for (int index = 0; index < graph.Nodes.Count; index++)
            {
                GraphNodeConfiguration node = graph.Nodes[index];
                if (!connected.Contains(node.Id))
                {
                    errors.Add(WellFormedness(
                        ValidationPointer.Node(name, index),
                        $"the node '{node.Id}' is an orphan: no edge reaches it and no edge leaves it"));
                }
            }
        }
    }
}

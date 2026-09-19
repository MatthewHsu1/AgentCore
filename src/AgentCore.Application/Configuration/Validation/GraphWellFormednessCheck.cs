using System.Globalization;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>Check 7: one start node, no orphan, and a path from every node to an output.</summary>
internal static class GraphWellFormednessCheck
{
    public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        foreach (var (name, entry) in configuration.Entries)
        {
            if (entry.Graph is { } graph && graph.Nodes.Count > 0)
            {
                CheckGraph(name, graph, errors);
            }
        }
    }

    private static void CheckGraph(string name, GraphConfiguration graph, List<ConfigurationError> errors)
    {
        var starts = graph.Nodes.Count(static node => node.Start);
        if (starts != 1)
        {
            errors.Add(WellFormedness(
                ValidationPointer.GraphNodes(name),
                string.Create(CultureInfo.InvariantCulture, $"the graph in entry '{name}' declares {starts} start nodes, and check 7 needs exactly one")));
        }

        CheckOrphans(name, graph, errors);

        var outputs = graph.Nodes.Where(static node => node.Output).Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (outputs.Count == 0)
        {
            errors.Add(WellFormedness(ValidationPointer.GraphNodes(name), "the graph declares no output node, so no path reaches an output"));
            return;
        }

        var forward = GraphReach.BuildAdjacency(graph);
        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            var node = graph.Nodes[index];
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
        var connected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            connected.Add(edge.From);
            connected.Add(edge.To);
        }

        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            var node = graph.Nodes[index];
            if (!connected.Contains(node.Id))
            {
                errors.Add(WellFormedness(
                    ValidationPointer.Node(name, index),
                    $"the node '{node.Id}' is an orphan: no edge reaches it and no edge leaves it"));
            }
        }
    }
}

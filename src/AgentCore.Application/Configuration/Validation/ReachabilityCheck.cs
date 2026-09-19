using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>Check 6: every stage is reachable from the initial stage, and every node from the start node.</summary>
internal static class ReachabilityCheck
{
    public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        foreach (var (name, entry) in configuration.Entries)
        {
            if (entry.Policy is { } policy)
            {
                CheckPolicy(name, policy, errors);
            }

            if (entry.Graph is { } graph && graph.Nodes.Count > 0)
            {
                CheckGraph(name, graph, errors);
            }
        }
    }

    private static void CheckPolicy(string name, PolicyConfiguration policy, List<ConfigurationError> errors)
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var stage in policy.Stages)
        {
            edges[stage.Id] = [.. stage.To.Select(static transition => transition.Stage)];
        }

        var reachable = GraphReach.Reach(policy.Initial, edges);

        for (var index = 0; index < policy.Stages.Count; index++)
        {
            var stage = policy.Stages[index];
            if (!reachable.Contains(stage.Id))
            {
                errors.Add(Reachability(
                    ValidationPointer.Stage(name, index),
                    $"the stage '{stage.Id}' is unreachable from the initial stage '{policy.Initial}' in entry '{name}'"));
            }

            if (!stage.Terminal && stage.To.Count == 0)
            {
                errors.Add(Reachability(
                    ValidationPointer.Stage(name, index),
                    $"the stage '{stage.Id}' is not terminal and has no exit"));
            }
        }
    }

    // A graph with zero or several start nodes is refused by check 7, so this check stays silent on it.
    private static void CheckGraph(string name, GraphConfiguration graph, List<ConfigurationError> errors)
    {
        var starts = graph.Nodes.Where(static node => node.Start).Select(static node => node.Id).ToList();
        if (starts.Count != 1)
        {
            return;
        }

        var live = GraphReach.Reach(starts[0], GraphReach.BuildAdjacency(graph));

        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            if (!live.Contains(graph.Nodes[index].Id))
            {
                errors.Add(Reachability(
                    ValidationPointer.Node(name, index),
                    $"the node '{graph.Nodes[index].Id}' is unreachable from the start node '{starts[0]}' in entry '{name}'"));
            }
        }
    }
}

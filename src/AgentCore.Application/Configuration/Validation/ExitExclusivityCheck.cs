using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// Check 5: exclusivity and coverage by evaluation. Gathers the sibling exits of each stage and
/// each graph node, then hands each set to <see cref="GuardExclusivityCheck"/>.
/// </summary>
internal static class ExitExclusivityCheck
{
    public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors, List<ConfigurationError> warnings)
    {
        var evaluator = new GuardEvaluator(configuration.Guards);

        foreach (var (name, entry) in configuration.Entries)
        {
            if (entry.Policy is { } policy)
            {
                CheckStages(configuration, name, policy, evaluator, errors, warnings);
            }

            if (entry.Graph is { } graph && graph.Edges.Count > 0)
            {
                CheckNodes(configuration, name, graph, evaluator, errors, warnings);
            }
        }
    }

    private static void CheckStages(
        AgentCoreConfiguration configuration,
        string name,
        PolicyConfiguration policy,
        GuardEvaluator evaluator,
        List<ConfigurationError> errors,
        List<ConfigurationError> warnings)
    {
        for (var index = 0; index < policy.Stages.Count; index++)
        {
            var stage = policy.Stages[index];
            var exits = new List<SiblingExit>(stage.To.Count);

            for (var exit = 0; exit < stage.To.Count; exit++)
            {
                var transition = stage.To[exit];
                exits.Add(Exit(transition.When, "exit", transition.Stage, ValidationPointer.Transition(name, index, exit)));
            }

            var pinned = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [ReservedStateSlots.Stage] = JsonValue.Create(stage.Id),
            };

            GuardExclusivityCheck.Run(
                new SiblingGroup(
                    exits,
                    ConfigurationError.AppendPointer(ValidationPointer.Stage(name, index), "to"),
                    $"the stage '{stage.Id}'",
                    pinned),
                evaluator,
                configuration.State,
                errors,
                warnings);
        }
    }

    private static void CheckNodes(
        AgentCoreConfiguration configuration,
        string name,
        GraphConfiguration graph,
        GuardEvaluator evaluator,
        List<ConfigurationError> errors,
        List<ConfigurationError> warnings)
    {
        var byFrom = graph.Edges
            .Select(static (edge, index) => (edge, index))
            .GroupBy(static pair => pair.edge.From, StringComparer.Ordinal);

        foreach (var group in byFrom)
        {
            var exits = group
                .Select(pair => Exit(pair.edge.When, "edge", pair.edge.To, ValidationPointer.Edge(name, pair.index)))
                .ToList();

            GuardExclusivityCheck.Run(
                new SiblingGroup(
                    exits,
                    ValidationPointer.GraphEdges(name),
                    $"the node '{group.Key}'",
                    new Dictionary<string, JsonNode?>(StringComparer.Ordinal)),
                evaluator,
                configuration.State,
                errors,
                warnings);
        }
    }

    private static SiblingExit Exit(GuardReference? guard, string kind, string target, string pointer)
        => new(
            DescribeExit(guard, kind, target),
            guard is null ? pointer : ConfigurationError.AppendPointer(pointer, "when"),
            guard);

    private static string DescribeExit(GuardReference? guard, string kind, string target)
        => guard switch
        {
            null => $"the unconditional {kind} to '{target}'",
            { Name: { } name } => $"the guard '{name}'",
            _ => $"the inline rule on the {kind} to '{target}'",
        };
}

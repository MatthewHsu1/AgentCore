using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>Check 4: guard operators and variables, over every rule the document holds.</summary>
internal static class GuardRuleCheck
{
    public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        foreach (var guard in configuration.Guards)
        {
            CheckOneRule(configuration, guard.Value, ValidationPointer.Guard(guard.Key), errors);
        }

        foreach (var slot in configuration.State)
        {
            if (slot.Value.Increment is { } increment)
            {
                CheckOneRule(configuration, increment, ConfigurationError.AppendPointer(ValidationPointer.State(slot.Key), "increment"), errors);
            }
        }

        foreach (var (name, entry) in configuration.Entries)
        {
            if (entry.Policy is { } policy)
            {
                CheckPolicyRules(configuration, name, policy, errors);
            }

            if (entry.Graph is { } graph)
            {
                CheckGraphRules(configuration, name, graph, errors);
            }
        }
    }

    private static void CheckPolicyRules(AgentCoreConfiguration configuration, string name, PolicyConfiguration policy, List<ConfigurationError> errors)
    {
        for (var index = 0; index < policy.Stages.Count; index++)
        {
            var stage = policy.Stages[index];
            for (var exit = 0; exit < stage.To.Count; exit++)
            {
                if (stage.To[exit].When?.Rule is { } rule)
                {
                    CheckOneRule(configuration, rule, ConfigurationError.AppendPointer(ValidationPointer.Transition(name, index, exit), "when"), errors);
                }
            }
        }
    }

    private static void CheckGraphRules(AgentCoreConfiguration configuration, string name, GraphConfiguration graph, List<ConfigurationError> errors)
    {
        for (var index = 0; index < graph.Edges.Count; index++)
        {
            if (graph.Edges[index].When?.Rule is { } rule)
            {
                CheckOneRule(configuration, rule, ConfigurationError.AppendPointer(ValidationPointer.Edge(name, index), "when"), errors);
            }
        }
    }

    private static void CheckOneRule(AgentCoreConfiguration configuration, JsonNode rule, string pointer, List<ConfigurationError> errors)
    {
        var facts = new GuardRuleFacts();
        facts.Collect(rule);

        foreach (var name in facts.Operators.Where(name => !GuardOperators.IsAllowed(name)))
        {
            errors.Add(Operators(pointer, GuardOperators.DescribeRejection(name)));
        }

        if (facts.HasDoubleNegationSugar)
        {
            errors.Add(Operators(pointer, GuardOperators.DoubleNegationSugarRejection));
        }

        foreach (var slot in facts.Variables.Where(slot =>
            !configuration.State.ContainsKey(slot) && !ReservedStateSlots.Contains(slot)))
        {
            errors.Add(Operators(pointer, $"the rule reads the slot '{slot}', and state: does not declare it"));
        }

        foreach (var comparison in facts.NumericComparisons)
        {
            if (configuration.State.TryGetValue(comparison.Value, out var slot) && slot.Type == StateSlotType.Boolean)
            {
                errors.Add(Operators(
                    pointer,
                    $"the operator '{comparison.Key}' compares the slot '{comparison.Value}', and that slot is a boolean rather than a number"));
            }
        }
    }
}

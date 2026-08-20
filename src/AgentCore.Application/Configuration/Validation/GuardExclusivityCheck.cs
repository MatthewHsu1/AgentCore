using System.Globalization;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>One exit of a stage or one edge of a graph, as check 5 reads it.</summary>
/// <param name="Description">How the message names this exit.</param>
/// <param name="Pointer">The JSON Pointer to the exit.</param>
/// <param name="Reference">The guard reference, or <see langword="null"/> for an unconditional exit.</param>
internal sealed record SiblingExit(string Description, string Pointer, GuardReference? Reference);

/// <summary>
/// Check 5 of section 8.5: exclusivity and coverage by evaluation.
/// </summary>
/// <remarks>
/// The validator enumerates the state domain and evaluates every sibling guard at every point. Two
/// true at one point is an error, and the message prints the state that triggers it. Zero true is
/// the normal case and means the stage stays where it is. This covers a stage exit and a graph edge,
/// because section 8.2 records that both silent MAF failures happen on both.
/// </remarks>
internal static class GuardExclusivityCheck
{
    /// <summary>Runs check 5 over one group of siblings.</summary>
    /// <param name="exits">The siblings, in document order.</param>
    /// <param name="groupPointer">The JSON Pointer to the group, used by the coverage warning.</param>
    /// <param name="groupDescription">How the warning names the group.</param>
    /// <param name="evaluator">The evaluator that resolves and runs a rule.</param>
    /// <param name="slots">The declared state slots.</param>
    /// <param name="pinned">Slots whose value the group already fixes, such as <c>stage</c>.</param>
    /// <param name="errors">Receives an overlap error.</param>
    /// <param name="warnings">Receives a partial-coverage warning.</param>
    public static void Run(
        IReadOnlyList<SiblingExit> exits,
        string groupPointer,
        string groupDescription,
        GuardEvaluator evaluator,
        IReadOnlyDictionary<string, StateSlotConfiguration> slots,
        IReadOnlyDictionary<string, JsonNode?> pinned,
        List<ConfigurationError> errors,
        List<ConfigurationError> warnings)
    {
        if (exits.Count < 2)
        {
            // One exit cannot overlap a sibling, and no exit is check 6's concern.
            return;
        }

        var rules = new JsonNode?[exits.Count];
        var facts = new GuardRuleFacts();
        for (var index = 0; index < exits.Count; index++)
        {
            rules[index] = evaluator.Resolve(exits[index].Reference);
            facts.Collect(rules[index]);
        }

        var domains = StateDomain.Build(facts, slots, pinned);
        var total = StateDomain.CountPoints(domains);

        IEnumerable<Dictionary<string, JsonNode?>> points;
        if (total > StateDomain.MaximumPoints)
        {
            points = StateDomain.Sample(domains, StateDomain.MaximumPoints);
            warnings.Add(new ConfigurationError
            {
                Pointer = groupPointer,
                Message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the state domain of {groupDescription} passes {StateDomain.MaximumPoints} points, so check 5 sampled {StateDomain.MaximumPoints} points at random and its coverage is partial"),
                Check = ConfigurationCheck.GuardExclusivity,
            });
        }
        else
        {
            points = StateDomain.Enumerate(domains);
        }

        foreach (var state in points)
        {
            var first = -1;
            for (var index = 0; index < rules.Length; index++)
            {
                var rule = rules[index];
                if (rule is null || !evaluator.Evaluate(rule, state))
                {
                    continue;
                }

                if (first < 0)
                {
                    first = index;
                    continue;
                }

                errors.Add(new ConfigurationError
                {
                    Pointer = exits[index].Pointer,
                    Message = $"{exits[first].Description} and {exits[index].Description} are both true at the same time. "
                              + $"The state that triggers it is {StateDomain.Describe(state)}.",
                    Check = ConfigurationCheck.GuardExclusivity,
                });

                // One overlap for one group is enough. A second point repeats the same defect.
                return;
            }
        }
    }
}

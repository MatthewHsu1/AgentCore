using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;

namespace AgentCore.Application.State;

/// <summary>
/// Writer 3 of section 8.3. It holds an integer that increments whenever its rule is true.
/// </summary>
/// <remarks>
/// The rule is a JSONLogic rule over the same state the guards read, so it runs through
/// <see cref="IGuardEvaluator"/>. Section 8.7: a rule that throws is not a defect, and the evaluator
/// logs it once and returns <see langword="false"/>.
/// </remarks>
public sealed class CounterStateWriter
{
    private readonly IGuardEvaluator _rules;

    /// <summary>Creates the counter writer.</summary>
    /// <param name="rules">The evaluator that runs an <c>increment</c> rule.</param>
    public CounterStateWriter(IGuardEvaluator rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>Increments every <c>writer: counter</c> slot whose rule is true.</summary>
    /// <param name="state">The state of one call.</param>
    /// <returns>The number of slots this writer incremented.</returns>
    public int Apply(StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // The rules read one snapshot, so every counter sees the same state.
        var snapshot = state.Snapshot();

        var incremented = 0;
        foreach (var (name, slot) in state.Configuration.State)
        {
            if (slot.Writer != StateWriter.Counter || slot.Increment is null)
            {
                continue;
            }

            if (!_rules.Evaluate(slot.Increment, snapshot))
            {
                continue;
            }

            var current = state.Read(name) is JsonValue value && value.TryGetValue(out long whole) ? whole : 0L;
            if (state.TryWrite(name, JsonValue.Create(current + 1)))
            {
                incremented++;
            }
        }

        return incremented;
    }
}

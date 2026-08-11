using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Json.Logic;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>
/// The test double for <see cref="IGuardEvaluator"/>.
/// </summary>
/// <remarks>
/// The real implementation belongs to the validator. This double exists so the policy runtime and the
/// counter writer can be tested on their own, and it holds the two rules section 8.7 fixes: a guard
/// that throws is not a defect, and a name the table does not hold is false at run time.
/// </remarks>
internal sealed class TestGuardEvaluator : IGuardEvaluator
{
    private readonly EquatableDictionary<JsonNode> _guards;

    public TestGuardEvaluator(AgentCoreConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _guards = configuration.Guards;
    }

    /// <summary>Gets how many guards this evaluator failed to run.</summary>
    public int Failures { get; private set; }

    public bool Evaluate(GuardReference guard, IReadOnlyDictionary<string, JsonNode?> state)
    {
        ArgumentNullException.ThrowIfNull(guard);

        if (guard.Rule is { } inline)
        {
            return Evaluate(inline, state);
        }

        return guard.Name is { } name && _guards.TryGetValue(name, out var named) && Evaluate(named, state);
    }

    public bool Evaluate(JsonNode rule, IReadOnlyDictionary<string, JsonNode?> state)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(state);

        JsonObject data = [];
        foreach (var (name, value) in state)
        {
            data[name] = value?.DeepClone();
        }

        try
        {
            return IsTruthy(JsonLogic.Apply(rule, data));
        }
        catch (JsonLogicException)
        {
            // Log once, treat the guard as false, and continue. Section 8.7.
            Failures++;
            return false;
        }
    }

    private static bool IsTruthy(JsonNode? result) => result switch
    {
        null => false,
        JsonValue value when value.TryGetValue(out bool flag) => flag,
        JsonValue value when value.TryGetValue(out double number) => number != 0,
        JsonValue value when value.TryGetValue(out string? text) => !string.IsNullOrEmpty(text),
        JsonArray list => list.Count > 0,
        _ => true,
    };
}

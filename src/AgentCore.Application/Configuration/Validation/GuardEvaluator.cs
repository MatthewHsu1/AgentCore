using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using Json.Logic;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// The one <see cref="IGuardEvaluator"/> of this release. It runs a rule through <c>JsonLogic</c>.
/// </summary>
/// <remarks>
/// <para>
/// Check 5 of section 8.5 uses this type at load time, and the stage machine uses it on every turn.
/// Both paths read the same <c>guards:</c> table, so a guard cannot mean one thing at load time and
/// another at run time.
/// </para>
/// <para>
/// The evaluator runs the <c>Rule</c> object model of <c>JsonLogic</c>, and not the newer
/// <c>JsonLogic.Apply(JsonNode, JsonNode)</c> entry point. Measured on package version 6.1.0, the
/// newer engine returns <see langword="null"/> for <c>min</c> and for <c>max</c> as soon as one
/// operand is a <c>var</c> read, so a guard built on either operator is false at every state. The
/// <c>Rule</c> model applies each argument before it compares, and it answers all twenty operators of
/// the section 8.4 allow-list correctly.
/// </para>
/// <para>
/// A guard is static configuration, so each rule of the <c>guards:</c> table deserializes exactly
/// once, when this type is constructed. A rule that arrives inline deserializes once for each node,
/// and the result is held by node identity. That removes the per-turn parse, which matters because a
/// guard runs on every turn of every call.
/// </para>
/// <para>
/// Section 8.7 fixes the failure behaviour: <c>JsonLogic</c> throws when a value cannot convert, and
/// check 4 cannot see every such case. This type catches the failure, reports it once for each guard,
/// treats the guard as false, and continues. It never propagates, because a failed guard must not
/// drop a call. A rule that fails to deserialize follows the same path: the constructor holds the
/// cause, names the guard, and reports it once, so a malformed rule never kills the process at
/// startup and never hides which guard is at fault.
/// </para>
/// </remarks>
public sealed class GuardEvaluator : IGuardEvaluator
{
    private static readonly JsonNode TrueRule = JsonValue.Create(true);

    private readonly IReadOnlyDictionary<string, JsonNode> _guards;
    private readonly Dictionary<string, CompiledGuard> _named;
    private readonly Dictionary<JsonNode, CompiledGuard> _inline = new(ReferenceEqualityComparer.Instance);
    private readonly Action<string, Exception>? _onFailure;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates an evaluator over a <c>guards:</c> table.</summary>
    /// <param name="guards">The named guards, keyed by guard name. Each value is a raw JSONLogic rule.</param>
    public GuardEvaluator(IReadOnlyDictionary<string, JsonNode> guards)
        : this(guards, null)
    {
    }

    /// <summary>Creates an evaluator over a <c>guards:</c> table, with a failure handler.</summary>
    /// <param name="guards">The named guards, keyed by guard name. Each value is a raw JSONLogic rule.</param>
    /// <param name="onFailure">
    /// Receives the guard text and the cause the first time that guard fails. Section 8.7 logs once,
    /// so this handler runs once for each distinct guard and never for a repeat of the same guard. A
    /// guard whose rule does not deserialize reports here under its name, while the constructor runs.
    /// </param>
    public GuardEvaluator(IReadOnlyDictionary<string, JsonNode> guards, Action<string, Exception>? onFailure)
    {
        ArgumentNullException.ThrowIfNull(guards);

        _guards = guards;
        _onFailure = onFailure;
        _named = new Dictionary<string, CompiledGuard>(guards.Count, StringComparer.Ordinal);

        // The unconditional exit is a rule too, and check 5 resolves it as one. Compile it here so
        // the hot loop of check 5 never parses it.
        _inline[TrueRule] = Compile(TrueRule);

        foreach (var entry in guards)
        {
            var compiled = Compile(entry.Value);
            _named[entry.Key] = compiled;

            // Check 5 resolves a name to its node and then evaluates that node, so the same node
            // reaches the inline path. Share the one compiled rule rather than parse it twice.
            _inline[entry.Value] = compiled;

            if (compiled.Failure is { } failure)
            {
                ReportOnce(entry.Key, failure);
            }
        }
    }

    /// <summary>Gets the rule of a named guard.</summary>
    /// <param name="name">The guard name.</param>
    /// <param name="rule">The raw rule, when the table holds the name.</param>
    /// <returns><see langword="true"/> when the table holds the name.</returns>
    public bool TryGetGuard(string name, out JsonNode? rule)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_guards.TryGetValue(name, out var found))
        {
            rule = found;
            return true;
        }

        rule = null;
        return false;
    }

    /// <inheritdoc />
    public bool Evaluate(GuardReference guard, IReadOnlyDictionary<string, JsonNode?> state)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(state);

        var compiled = ResolveCompiled(guard);
        if (compiled is null)
        {
            // A name the table does not hold is a check 2 failure at load time. At run time it is false.
            return false;
        }

        return Apply(compiled, state, guard.ToString());
    }

    /// <inheritdoc />
    public bool Evaluate(JsonNode rule, IReadOnlyDictionary<string, JsonNode?> state)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(state);

        return Apply(CompileInline(rule), state, rule.ToJsonString());
    }

    /// <summary>Resolves a reference to the rule it stands for.</summary>
    /// <param name="guard">The reference. A <see langword="null"/> reference is the unconditional exit.</param>
    /// <returns>The raw rule, or <see langword="null"/> when a name does not resolve.</returns>
    public JsonNode? Resolve(GuardReference? guard)
    {
        if (guard is null)
        {
            // Section 8.5 evaluates an unconditional exit as a sibling that is always true.
            return TrueRule;
        }

        if (guard.Name is null)
        {
            return guard.Rule;
        }

        return _guards.TryGetValue(guard.Name, out var rule) ? rule : null;
    }

    /// <summary>Builds the data document one rule reads.</summary>
    /// <param name="state">The slot values, keyed by slot name.</param>
    /// <returns>A fresh object. Every value is cloned, so no node keeps a second parent.</returns>
    public static JsonObject BuildData(IReadOnlyDictionary<string, JsonNode?> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var data = new JsonObject();
        foreach (var entry in state)
        {
            data[entry.Key] = entry.Value?.DeepClone();
        }

        return data;
    }

    private CompiledGuard? ResolveCompiled(GuardReference guard)
    {
        if (guard.Name is { } name)
        {
            return _named.TryGetValue(name, out var found) ? found : null;
        }

        return guard.Rule is { } rule ? CompileInline(rule) : null;
    }

    private CompiledGuard CompileInline(JsonNode rule)
    {
        lock (_gate)
        {
            if (_inline.TryGetValue(rule, out var found))
            {
                return found;
            }

            var compiled = Compile(rule);
            _inline[rule] = compiled;
            return compiled;
        }
    }

    private static CompiledGuard Compile(JsonNode? rule)
    {
        try
        {
            return new CompiledGuard(JsonSerializer.Deserialize<Rule>(rule), null);
        }
#pragma warning disable CA1031 // Section 8.7: a rule that will not parse is false, and the call continues.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new CompiledGuard(null, exception);
        }
    }

    private bool Apply(CompiledGuard compiled, IReadOnlyDictionary<string, JsonNode?> state, string description)
    {
        if (compiled.Failure is { } failure)
        {
            ReportOnce(description, failure);
            return false;
        }

        try
        {
            var result = compiled.Parsed?.Apply(BuildData(state));
            return result is not null && result.IsTruthy();
        }
#pragma warning disable CA1031 // Section 8.7: a guard that throws is not a defect. It is false, and the call continues.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            ReportOnce(description, exception);
            return false;
        }
    }

    private void ReportOnce(string description, Exception exception)
    {
        if (_onFailure is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_reported.Add(description))
            {
                return;
            }
        }

        _onFailure(description, exception);
    }

    /// <summary>One rule of the table, parsed once, or the cause that stopped it parsing.</summary>
    /// <param name="Parsed">The parsed rule, or <see langword="null"/> when the rule did not parse.</param>
    /// <param name="Failure">The cause, or <see langword="null"/> when the rule parsed.</param>
    private sealed record CompiledGuard(Rule? Parsed, Exception? Failure);
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// Everything checks 4 and 5 read out of one or more raw JSONLogic rules.
/// </summary>
/// <remarks>
/// One walk collects the operators a rule uses, the slots it names, the literals it compares each
/// slot with, and the numeric comparisons it makes. Check 4 reads the first three. Check 5 reads the
/// literals, because they are the thresholds the state domain buckets around.
/// </remarks>
internal sealed class GuardRuleFacts
{
    /// <summary>Gets every operator the rule uses, in first-seen order.</summary>
    public List<string> Operators { get; } = [];

    /// <summary>Gets every slot the rule names through <c>var</c> or <c>missing</c>, in first-seen order.</summary>
    public List<string> Variables { get; } = [];

    /// <summary>Gets the literals the rule compares each slot with, keyed by slot name.</summary>
    public Dictionary<string, List<JsonNode>> Literals { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets every numeric comparison, as the operator and the slot it reads.</summary>
    public List<KeyValuePair<string, string>> NumericComparisons { get; } = [];

    /// <summary>
    /// Reports whether the rule writes <c>!!</c> in the unary sugar form, as <c>{"!!": x}</c>.
    /// </summary>
    /// <remarks>
    /// The JSONLogic specification allows the sugar, and this library implements it for <c>!</c> and
    /// not for <c>!!</c>. Both engines of package version 6.1.0 fail on the sugar form, so check 4
    /// rejects it and names the working form. <c>!!</c> stays on the section 8.4 allow-list.
    /// </remarks>
    public bool HasDoubleNegationSugar { get; private set; }

    private readonly HashSet<string> _seenOperators = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenVariables = new(StringComparer.Ordinal);

    /// <summary>Walks one rule and adds what it holds.</summary>
    /// <param name="rule">The raw rule. A <see langword="null"/> rule adds nothing.</param>
    public void Collect(JsonNode? rule) => Walk(rule);

    /// <summary>Reads the slot name out of a <c>{ var: name }</c> node.</summary>
    /// <param name="node">The node to read.</param>
    /// <returns>The slot name, or <see langword="null"/> when the node is not a variable read.</returns>
    public static string? TryReadVariable(JsonNode? node)
    {
        if (node is not JsonObject holder || holder.Count != 1)
        {
            return null;
        }

        return holder.TryGetPropertyValue("var", out var path) ? ReadVariableName(path) : null;
    }

    private void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject holder:
                foreach (var entry in holder)
                {
                    WalkOperator(entry.Key, entry.Value);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item);
                }

                break;

            default:
                // A scalar is a literal and holds nothing to collect.
                break;
        }
    }

    private void WalkOperator(string name, JsonNode? argument)
    {
        if (_seenOperators.Add(name))
        {
            Operators.Add(name);
        }

        if (string.Equals(name, "var", StringComparison.Ordinal))
        {
            AddVariable(ReadVariableName(argument));
            return;
        }

        if (string.Equals(name, "missing", StringComparison.Ordinal))
        {
            foreach (var slot in ReadNames(argument))
            {
                AddVariable(slot);
            }

            return;
        }

        if (string.Equals(name, "!!", StringComparison.Ordinal) && argument is not JsonArray)
        {
            HasDoubleNegationSugar = true;
        }

        List<JsonNode?> operands = argument is JsonArray array ? [.. array] : [argument];
        RecordComparison(name, operands);

        foreach (var operand in operands)
        {
            Walk(operand);
        }
    }

    private void RecordComparison(string name, List<JsonNode?> operands)
    {
        var isComparison = GuardOperators.IsNumericComparison(name)
                           || string.Equals(name, "===", StringComparison.Ordinal)
                           || string.Equals(name, "!==", StringComparison.Ordinal)
                           || string.Equals(name, "in", StringComparison.Ordinal);

        if (!isComparison)
        {
            return;
        }

        var slots = new List<string>();
        var literals = new List<JsonNode>();

        foreach (var operand in operands)
        {
            var slot = TryReadVariable(operand);
            if (slot is not null)
            {
                slots.Add(slot);
                continue;
            }

            switch (operand)
            {
                case JsonValue value:
                    literals.Add(value);
                    break;

                case JsonArray members:
                    foreach (var member in members)
                    {
                        if (member is JsonValue memberValue)
                        {
                            literals.Add(memberValue);
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        foreach (var slot in slots)
        {
            if (GuardOperators.IsNumericComparison(name))
            {
                NumericComparisons.Add(new KeyValuePair<string, string>(name, slot));
            }

            if (literals.Count == 0)
            {
                continue;
            }

            if (!Literals.TryGetValue(slot, out var bucket))
            {
                bucket = [];
                Literals[slot] = bucket;
            }

            bucket.AddRange(literals);
        }
    }

    private void AddVariable(string? name)
    {
        if (name is null)
        {
            return;
        }

        if (_seenVariables.Add(name))
        {
            Variables.Add(name);
        }
    }

    private static string? ReadVariableName(JsonNode? path)
        => path switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonArray array when array.Count > 0 => ReadVariableName(array[0]),
            _ => null,
        };

    private static IEnumerable<string> ReadNames(JsonNode? argument)
    {
        switch (argument)
        {
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                yield return value.GetValue<string>();
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    var name = ReadVariableName(item);
                    if (name is not null)
                    {
                        yield return name;
                    }
                }

                break;

            default:
                break;
        }
    }
}

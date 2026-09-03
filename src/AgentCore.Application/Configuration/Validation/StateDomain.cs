using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>One slot and every value check 5 gives it.</summary>
/// <param name="Name">The slot name.</param>
/// <param name="Points">The values, in a stable order.</param>
internal sealed record SlotDomain(string Name, IReadOnlyList<JsonNode?> Points);

/// <summary>
/// The state-domain enumeration of check 5, section 8.5.
/// </summary>
/// <remarks>
/// A boolean gives two points. An enum gives its members. A number is bucketed below, at, and above
/// each threshold the sibling rules mention. A string that no rule compares is not enumerated. Only
/// the slots the sibling rules name enter the product, so five booleans give 32 points.
/// </remarks>
internal static class StateDomain
{
    /// <summary>The point count above which check 5 samples instead of enumerating.</summary>
    public const int MaximumPoints = 65536;

    /// <summary>The value a string slot takes to stand for "any value the rules do not name".</summary>
    public const string OtherString = "\0agentcore-other";

    private const int SampleSeed = 20250811;

    /// <summary>Builds the domain of every slot the sibling rules name.</summary>
    /// <param name="facts">What the walk found in the sibling rules.</param>
    /// <param name="slots">The declared slots, keyed by name.</param>
    /// <param name="pinned">Slots whose value is already known, such as <c>stage</c> on a stage exit.</param>
    /// <returns>One domain for each named slot, in first-seen order.</returns>
    public static List<SlotDomain> Build(
        GuardRuleFacts facts,
        IReadOnlyDictionary<string, StateSlotConfiguration> slots,
        IReadOnlyDictionary<string, JsonNode?> pinned)
    {
        var domains = new List<SlotDomain>();

        foreach (var name in facts.Variables)
        {
            if (pinned.TryGetValue(name, out var fixedValue))
            {
                domains.Add(new SlotDomain(name, [fixedValue]));
                continue;
            }

            facts.Literals.TryGetValue(name, out var literals);
            domains.Add(new SlotDomain(name, BuildPoints(name, slots, literals)));
        }

        return domains;
    }

    /// <summary>Counts the points the product holds, and stops counting above a ceiling.</summary>
    /// <param name="domains">The slot domains.</param>
    /// <returns>The product, capped so it cannot overflow.</returns>
    public static long CountPoints(IReadOnlyList<SlotDomain> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);

        var total = 1L;
        foreach (var domain in domains)
        {
            if (domain.Points.Count == 0)
            {
                return 0;
            }

            total *= domain.Points.Count;
            if (total > MaximumPoints)
            {
                return long.MaxValue;
            }
        }

        return total;
    }

    /// <summary>Walks the whole product, one state at a time.</summary>
    /// <param name="domains">The slot domains.</param>
    /// <returns>Every point of the product.</returns>
    public static IEnumerable<Dictionary<string, JsonNode?>> Enumerate(IReadOnlyList<SlotDomain> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);

        if (domains.Count == 0)
        {
            yield return new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            yield break;
        }

        var cursor = new int[domains.Count];
        while (true)
        {
            yield return Materialise(domains, cursor);

            var index = domains.Count - 1;
            while (index >= 0)
            {
                cursor[index]++;
                if (cursor[index] < domains[index].Points.Count)
                {
                    break;
                }

                cursor[index] = 0;
                index--;
            }

            if (index < 0)
            {
                yield break;
            }
        }
    }

    /// <summary>Takes a fixed number of points at random out of a product that is too large to walk.</summary>
    /// <param name="domains">The slot domains.</param>
    /// <param name="count">How many points to take.</param>
    /// <returns>The sampled points. The seed is fixed, so a build repeats.</returns>
    public static IEnumerable<Dictionary<string, JsonNode?>> Sample(IReadOnlyList<SlotDomain> domains, int count)
    {
        ArgumentNullException.ThrowIfNull(domains);

        // The seed is fixed so that a failing build fails the same way twice.
        var random = new Random(SampleSeed);
        var cursor = new int[domains.Count];

        for (var taken = 0; taken < count; taken++)
        {
            for (var index = 0; index < domains.Count; index++)
            {
                cursor[index] = random.Next(domains[index].Points.Count);
            }

            yield return Materialise(domains, cursor);
        }
    }

    /// <summary>Writes one point as the JSON object a message prints.</summary>
    /// <param name="state">The point.</param>
    /// <returns>The JSON text, with the slot names in order.</returns>
    public static string Describe(IReadOnlyDictionary<string, JsonNode?> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var holder = new JsonObject();
        foreach (var entry in state)
        {
            var value = entry.Value;
            if (value is JsonValue text
                && value.GetValueKind() == JsonValueKind.String
                && string.Equals(text.GetValue<string>(), OtherString, StringComparison.Ordinal))
            {
                holder[entry.Key] = "<any other string>";
                continue;
            }

            holder[entry.Key] = value?.DeepClone();
        }

        return holder.ToJsonString();
    }

    private static Dictionary<string, JsonNode?> Materialise(IReadOnlyList<SlotDomain> domains, int[] cursor)
    {
        var state = new Dictionary<string, JsonNode?>(domains.Count, StringComparer.Ordinal);
        for (var index = 0; index < domains.Count; index++)
        {
            state[domains[index].Name] = domains[index].Points[cursor[index]];
        }

        return state;
    }

    private static IReadOnlyList<JsonNode?> BuildPoints(
        string name,
        IReadOnlyDictionary<string, StateSlotConfiguration> slots,
        List<JsonNode>? literals)
    {
        if (!slots.TryGetValue(name, out var slot))
        {
            var reserved = ReservedStateSlots.Contains(name) ? ReservedSlot(name) : null;
            if (reserved is null)
            {
                // Check 4 already reports an undeclared slot. One point keeps the product finite.
                return [null];
            }

            slot = reserved;
        }

        if (slot.EnumValues is { Count: > 0 } members)
        {
            List<JsonNode?> points = [.. members.Select(static member => (JsonNode?)member.DeepClone())];

            // A slot with no default reads as null until a writer fills it, and that is a state the
            // guards run in. Without this point check 5 analyses a domain the call never starts in.
            if (slot.Default is null)
            {
                points.Add(null);
            }

            return points;
        }

        return slot.Type switch
        {
            StateSlotType.Boolean => [JsonValue.Create(false), JsonValue.Create(true)],
            StateSlotType.Integer or StateSlotType.Number => NumericPoints(slot, literals),
            _ => StringPoints(slot, literals),
        };
    }

    private static StateSlotConfiguration ReservedSlot(string name)
        => name switch
        {
            ReservedStateSlots.TurnIndex => new StateSlotConfiguration
            {
                Type = StateSlotType.Integer,
                Writer = StateWriter.Const,
                Default = JsonValue.Create(0),
            },
            ReservedStateSlots.CallDurationSeconds => new StateSlotConfiguration
            {
                Type = StateSlotType.Number,
                Writer = StateWriter.Const,
                Default = JsonValue.Create(0),
            },
            _ => new StateSlotConfiguration
            {
                Type = StateSlotType.String,
                Writer = StateWriter.Const,
                Default = null,
            },
        };

    private static IReadOnlyList<JsonNode?> NumericPoints(StateSlotConfiguration slot, List<JsonNode>? literals)
    {
        var thresholds = new SortedSet<decimal>();
        foreach (var literal in literals ?? [])
        {
            if (literal is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
            {
                thresholds.Add(value.GetValue<decimal>());
            }
        }

        if (thresholds.Count == 0)
        {
            // No rule mentions a threshold, so the slot cannot split any sibling. One point is enough.
            return [slot.Default?.DeepClone() ?? JsonValue.Create(0)];
        }

        var points = new SortedSet<decimal>();
        foreach (var threshold in thresholds)
        {
            points.Add(threshold - 1);
            points.Add(threshold);
            points.Add(threshold + 1);
        }

        return [.. points.Select(static point => (JsonNode?)JsonValue.Create(point))];
    }

    private static List<JsonNode?> StringPoints(StateSlotConfiguration slot, List<JsonNode>? literals)
    {
        var members = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var literal in literals ?? [])
        {
            if (literal is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                var text = value.GetValue<string>();
                if (seen.Add(text))
                {
                    members.Add(text);
                }
            }
        }

        if (members.Count == 0)
        {
            // Section 8.5: a string that no rule compares is not enumerated.
            return [slot.Default?.DeepClone()];
        }

        var points = new List<JsonNode?>(members.Count + 1);
        foreach (var member in members)
        {
            points.Add(JsonValue.Create(member));
        }

        points.Add(JsonValue.Create(OtherString));
        return points;
    }
}

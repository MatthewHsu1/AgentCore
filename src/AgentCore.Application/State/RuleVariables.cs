using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.State;

/// <summary>
/// Collects the slot names a JSONLogic rule reads through <c>var</c>.
/// </summary>
/// <remarks>
/// The unfilled-slot reminder needs to know which slots a stage waits on, and a stage waits on the
/// slots its exit guards read. Section 8.4 bans the iteration operators, so the walk is finite.
/// </remarks>
internal static class RuleVariables
{
    /// <summary>Adds every <c>var</c> name a rule reads to a set.</summary>
    /// <param name="rule">The raw rule, or <see langword="null"/>.</param>
    /// <param name="names">The set to fill.</param>
    public static void Collect(JsonNode? rule, ISet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        switch (rule)
        {
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    if (string.Equals(key, "var", StringComparison.Ordinal))
                    {
                        AddName(value, names);
                        continue;
                    }

                    Collect(value, names);
                }

                break;

            case JsonArray list:
                foreach (var item in list)
                {
                    Collect(item, names);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>Adds every slot the exits of one stage read.</summary>
    /// <param name="configuration">The loaded document, which holds the named guards.</param>
    /// <param name="stage">The stage.</param>
    /// <param name="names">The set to fill.</param>
    public static void CollectStageExits(AgentCoreConfiguration configuration, StageConfiguration stage, ISet<string> names)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(stage);

        foreach (var exit in stage.To)
        {
            if (exit.When is null)
            {
                continue;
            }

            var rule = exit.When.Rule;
            if (rule is null
                && exit.When.Name is { } guardName
                && configuration.Guards.TryGetValue(guardName, out var named))
            {
                rule = named;
            }

            Collect(rule, names);
        }
    }

    private static void AddName(JsonNode? value, ISet<string> names)
    {
        switch (value)
        {
            case JsonValue scalar when scalar.TryGetValue(out string? name) && !string.IsNullOrEmpty(name):
                names.Add(name);
                break;

            case JsonArray list when list.Count > 0:
                AddName(list[0], names);
                break;

            default:
                break;
        }
    }
}

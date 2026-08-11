using System.Text.Json.Nodes;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The value of a <c>when:</c> field. It is a guard name or an inline JSONLogic rule.
/// </summary>
/// <remarks>
/// Section 8.4 prefers a name, because the live mermaid render prints the name next to the edge and
/// an inline rule renders as an unreadable blob. Both forms are legal, so both are modelled.
/// </remarks>
public sealed record GuardReference
{
    private GuardReference(string? name, JsonNode? rule)
    {
        Name = name;
        Rule = rule;
    }

    /// <summary>Gets the name of a guard in <c>guards:</c>, or <see langword="null"/> when the reference is inline.</summary>
    public string? Name { get; }

    /// <summary>Gets the raw inline JSONLogic rule, or <see langword="null"/> when the reference is a name.</summary>
    public JsonNode? Rule { get; }

    /// <summary>Reports whether the reference names a guard in <c>guards:</c>.</summary>
    public bool IsNamed => Name is not null;

    /// <summary>Builds a reference to a named guard.</summary>
    /// <param name="name">The guard name.</param>
    /// <returns>The reference.</returns>
    public static GuardReference FromName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new GuardReference(name, null);
    }

    /// <summary>Builds a reference that carries an inline rule.</summary>
    /// <param name="rule">The raw JSONLogic rule.</param>
    /// <returns>The reference.</returns>
    public static GuardReference FromRule(JsonNode rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new GuardReference(null, rule);
    }

    /// <summary>Compares this reference with another reference. An inline rule compares by its JSON content.</summary>
    /// <param name="other">The other reference.</param>
    /// <returns><see langword="true"/> when both references are equal.</returns>
    public bool Equals(GuardReference? other)
        => other is not null
           && string.Equals(Name, other.Name, StringComparison.Ordinal)
           && JsonNode.DeepEquals(Rule, other.Rule);

    /// <inheritdoc />
    public override int GetHashCode() => Name is null ? 0 : StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>Writes the guard name, or the inline rule as JSON text.</summary>
    /// <returns>A readable form of the reference.</returns>
    public override string ToString() => Name ?? Rule?.ToJsonString() ?? string.Empty;
}

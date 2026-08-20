using System.Collections.Frozen;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// The JSONLogic operator allow-list of section 8.4.
/// </summary>
public static class GuardOperators
{
    private static readonly FrozenSet<string> AllowedSet = new[]
    {
        "var", "missing", "if", "===", "!==", ">", ">=", "<", "<=", "!", "!!",
        "and", "or", "in", "+", "-", "*", "/", "min", "max",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> RejectedSet = new[]
    {
        "==", "!=", "log", "map", "filter", "reduce", "all", "some", "none", "merge", "cat", "substr",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> Replacements = new Dictionary<string, string>
    {
        ["=="] = "===",
        ["!="] = "!==",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> NumericComparisonSet = new[]
    {
        ">", ">=", "<", "<=",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] AllowedOrder =
    [
        "var", "missing", "if", "===", "!==", ">", ">=", "<", "<=", "!", "!!",
        "and", "or", "in", "+", "-", "*", "/", "min", "max",
    ];

    private static readonly string[] RejectedOrder =
    [
        "==", "!=", "log", "map", "filter", "reduce", "all", "some", "none", "merge", "cat", "substr",
    ];

    /// <summary>Gets every operator a guard may use, in the order section 8.4 lists them.</summary>
    public static IReadOnlyList<string> Allowed => AllowedOrder;

    /// <summary>Gets every operator section 8.4 names as rejected, in the order it lists them.</summary>
    public static IReadOnlyList<string> Rejected => RejectedOrder;

    /// <summary>Gets the message check 4 reports for the unary sugar form of <c>!!</c>.</summary>
    public static string DoubleNegationSugarRejection
        => "the operator '!!' is written in the unary sugar form, and JsonLogic reads that form for "
           + "'!' only. Use '{\"!!\": [ x ]}' instead.";

    /// <summary>Reports whether a guard may use an operator.</summary>
    /// <param name="name">The operator name, as it appears in the rule.</param>
    /// <returns><see langword="true"/> when the operator is on the allow-list.</returns>
    public static bool IsAllowed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return AllowedSet.Contains(name);
    }

    /// <summary>Reports whether section 8.4 names an operator as rejected.</summary>
    /// <param name="name">The operator name, as it appears in the rule.</param>
    /// <returns><see langword="true"/> when the operator is on the rejected list.</returns>
    public static bool IsNamedRejected(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return RejectedSet.Contains(name);
    }

    /// <summary>Reports whether an operator compares two numbers.</summary>
    /// <param name="name">The operator name, as it appears in the rule.</param>
    /// <returns><see langword="true"/> for <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, and <c>&lt;=</c>.</returns>
    public static bool IsNumericComparison(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return NumericComparisonSet.Contains(name);
    }

    /// <summary>Gets the allowed operator that replaces a rejected one.</summary>
    /// <param name="name">The rejected operator name.</param>
    /// <returns>The replacement, or <see langword="null"/> when the operator has none.</returns>
    public static string? ReplacementFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Replacements.TryGetValue(name, out var replacement) ? replacement : null;
    }

    /// <summary>Writes the message check 4 reports for an operator that is not on the allow-list.</summary>
    /// <param name="name">The operator name, as it appears in the rule.</param>
    /// <returns>The message. It names the replacement when one exists.</returns>
    public static string DescribeRejection(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var replacement = ReplacementFor(name);
        if (replacement is not null)
        {
            return $"the operator '{name}' is rejected because it is loose equality. Use '{replacement}' instead.";
        }

        return $"the operator '{name}' is outside the section 8.4 allow-list";
    }
}

using System.Text.RegularExpressions;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Treats a short letter run followed by a short digit run as a code the answer must contain.
/// </summary>
public sealed partial class IdentifierCodeAnalyzer : IKnowledgeQueryAnalyzer
{
    /// <summary>The name <c>providers.knowledge.analyzer</c> selects this by.</summary>
    public const string AnalyzerName = "identifier-codes";

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word { get; }

    [GeneratedRegex("^[a-z]{1,4}[0-9]{1,3}$")]
    private static partial Regex Identifier { get; }

    /// <inheritdoc />
    public string Name => AnalyzerName;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<string> RequiredTerms(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return
        [
            .. Word.Matches(query.ToLowerInvariant())
                .Select(match => match.Value)
                .Where(token => Identifier.IsMatch(token))
                .Distinct(StringComparer.Ordinal),
        ];
    }
}

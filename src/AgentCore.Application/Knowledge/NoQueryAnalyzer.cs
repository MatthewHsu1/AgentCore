namespace AgentCore.Application.Knowledge;

/// <summary>Requires nothing, so vector similarity alone decides.</summary>
public sealed class NoQueryAnalyzer : IKnowledgeQueryAnalyzer
{
    /// <summary>The name <c>providers.knowledge.analyzer</c> selects this by.</summary>
    public const string AnalyzerName = "none";

    /// <inheritdoc />
    public string Name => AnalyzerName;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<string> RequiredTerms(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return [];
    }
}

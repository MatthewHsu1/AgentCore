namespace AgentCore.Application.Knowledge;

/// <summary>
/// Picks the terms one search must match, on top of vector similarity.
/// </summary>
public interface IKnowledgeQueryAnalyzer
{
    /// <summary>Gets the name <c>providers.knowledge.analyzer</c> selects this by.</summary>
    string Name { get; }

    /// <summary>Reads the terms one query requires.</summary>
    IReadOnlyList<string> RequiredTerms(string query);
}

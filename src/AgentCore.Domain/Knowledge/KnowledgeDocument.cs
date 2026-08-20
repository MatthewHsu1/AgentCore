namespace AgentCore.Domain.Knowledge;

/// <summary>
/// One whole knowledge-base document.
/// </summary>
public sealed record KnowledgeDocument
{
    /// <summary>Gets the id the search result named.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Gets the whole text of the document.</summary>
    public required string Text { get; init; }
}

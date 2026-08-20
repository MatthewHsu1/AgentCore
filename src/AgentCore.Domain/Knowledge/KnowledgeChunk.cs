namespace AgentCore.Domain.Knowledge;

/// <summary>
/// One ranked passage of one knowledge-base document.
/// </summary>
public sealed record KnowledgeChunk
{
    /// <summary>Gets the document the passage comes from. <c>knowledge.read</c> reads it back.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Gets the passage itself.</summary>
    public required string Text { get; init; }

    /// <summary>Gets how well the passage answers the query. A larger number ranks first.</summary>
    public required double Score { get; init; }
}

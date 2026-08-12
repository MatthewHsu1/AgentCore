namespace AgentCore.Application.Tools;

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

/// <summary>
/// The knowledge base the two built-in tools read.
/// </summary>
/// <remarks>
/// <para>
/// <c>knowledge.search</c> ranks passages and <c>knowledge.read</c> opens one document, so the model
/// finds a passage first and reads the whole page when the passage is not enough.
/// <c>providers.knowledge</c> picks the adapter that binds here.
/// </para>
/// <para>
/// An adapter may throw. The built-in tool that calls it turns the failure into an error result, so
/// section 8.7 holds whatever the store does.
/// </para>
/// </remarks>
public interface IKnowledgePort
{
    /// <summary>Ranks the passages that answer one query.</summary>
    /// <param name="query">What the model is looking for.</param>
    /// <param name="limit">The largest number of passages to return.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The passages, best first. It is empty when nothing matches.</returns>
    ValueTask<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one whole document.</summary>
    /// <param name="documentId">The id a search result named.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document, or <see langword="null"/> when the store holds no such id.</returns>
    ValueTask<KnowledgeDocument?> ReadAsync(string documentId, CancellationToken cancellationToken = default);
}

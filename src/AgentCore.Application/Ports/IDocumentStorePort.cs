using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Ports;

/// <summary>
/// The document half of the knowledge base, which <c>knowledge.read</c> opens.
/// </summary>
/// <remarks>
/// <para>
/// <c>knowledge.search</c> ranks passages and <c>knowledge.read</c> opens one document, so the model
/// finds a passage first and reads the whole page when the passage is not enough. This port opens,
/// and <see cref="IKnowledgeRetrievalPort"/> ranks. <c>providers.knowledge</c> picks the adapter
/// that binds here.
/// </para>
/// <para>
/// The two halves are separate ports because a vendor that supplies only search must not have to
/// read files as well. One adapter may still answer both, and the file store does.
/// </para>
/// <para>
/// An adapter may throw. The built-in tool that calls it turns the failure into an error result, so
/// section 8.7 holds whatever the store does.
/// </para>
/// </remarks>
public interface IDocumentStorePort
{
    /// <summary>Reads one whole document.</summary>
    /// <param name="documentId">The id a search result named.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document, or <see langword="null"/> when the store holds no such id.</returns>
    ValueTask<KnowledgeDocument?> ReadAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Names the documents the store holds.</summary>
    /// <param name="pattern">
    /// A glob expression over document ids, such as <c>policies/**/*.md</c>, or
    /// <see langword="null"/> to name every document.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>
    /// The ids in ordinal order, so two calls answer the same. An implementation names at most 200
    /// ids and sets <see cref="DocumentListing.Truncated"/> when the cap cut the answer.
    /// </returns>
    ValueTask<DocumentListing> ListAsync(string? pattern = null, CancellationToken cancellationToken = default);

    /// <summary>Finds the lines that match one pattern.</summary>
    /// <param name="pattern">The regular expression each line is matched against.</param>
    /// <param name="glob">
    /// A glob expression over document ids, such as <c>policies/**/*.md</c>, that says which
    /// documents to read, or <see langword="null"/> to read every document.
    /// </param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>
    /// The matches in ordinal order of document id and then by line, so two calls answer the same.
    /// An implementation returns at most 100 matches and sets <see cref="GrepResult.Truncated"/>
    /// when the cap cut the answer.
    /// </returns>
    ValueTask<GrepResult> GrepAsync(string pattern, string? glob = null, CancellationToken cancellationToken = default);
}

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
}

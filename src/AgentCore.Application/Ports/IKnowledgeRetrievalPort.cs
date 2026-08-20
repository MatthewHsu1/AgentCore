using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Ports;

/// <summary>
/// The ranking half of the knowledge base, which <c>knowledge.search</c> reads.
/// </summary>
/// <remarks>
/// <para>
/// <c>knowledge.search</c> ranks passages and <c>knowledge.read</c> opens one document, so the model
/// finds a passage first and reads the whole page when the passage is not enough. This port ranks,
/// and <see cref="IDocumentStorePort"/> opens. <c>providers.knowledge</c> picks the adapter that
/// binds here.
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
public interface IKnowledgeRetrievalPort
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
}

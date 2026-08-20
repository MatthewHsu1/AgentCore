using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;

/// <summary>
/// The ranking half of the knowledge base, over a Zilliz collection.
/// </summary>
/// <remarks>
/// <para>
/// This is path 2 of D7: <c>search_chunks</c> ranks in the vector store and returns a leaf path, and
/// <c>knowledge.read</c> then opens that path in the file store. The two halves therefore name a
/// document the same way, and the model reads back exactly what the search reported.
/// </para>
/// <para>
/// D14 puts the wire format in <see cref="ZillizCollection"/> and the embedding in
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>. This class is the seam between them: it
/// embeds the query, searches, and turns each row into one <see cref="KnowledgeChunk"/>. It owns no
/// connection and no key of its own.
/// </para>
/// </remarks>
public sealed class ZillizRetrievalStore : IKnowledgeRetrievalPort
{
    private readonly VectorStoreCollection<string, ZillizChunkRecord> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;

    /// <summary>Binds one collection and the generator that embeds a query for it.</summary>
    /// <param name="collection">The collection to rank in.</param>
    /// <param name="embeddings">The generator. Its model and its width must match the collection.</param>
    public ZillizRetrievalStore(
        VectorStoreCollection<string, ZillizChunkRecord> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddings)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(embeddings);

        _collection = collection;
        _embeddings = embeddings;
    }

    /// <summary>Gets the collection this store ranks in.</summary>
    /// <remarks>
    /// The adapter opens the collection and binds it here, and a test reads back what it opened it
    /// over. It is internal, so it adds nothing to the public surface.
    /// </remarks>
    internal VectorStoreCollection<string, ZillizChunkRecord> Collection => _collection;

    /// <summary>Gets the generator this store embeds every query with.</summary>
    /// <remarks>
    /// The adapter builds this generator from the two constants of section 3.1, and a test reads the
    /// model and the width back off it. It is internal, so it adds nothing to the public surface.
    /// </remarks>
    internal IEmbeddingGenerator<string, Embedding<float>> Embeddings => _embeddings;

    /// <summary>Ranks the passages that answer one query.</summary>
    /// <param name="query">What the model is looking for.</param>
    /// <param name="limit">The largest number of passages to return.</param>
    /// <param name="cancellationToken">Cancels the embedding and the search.</param>
    /// <returns>The passages, best first. It is empty when nothing matches.</returns>
    /// <remarks>
    /// A limit that asks for nothing costs no embedding call and no request, which is what the file
    /// store does as well.
    /// </remarks>
    public async ValueTask<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (limit <= 0)
        {
            return [];
        }

        var vector = await _embeddings
            .GenerateVectorAsync(query, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        List<KnowledgeChunk> chunks = [];
        var hits = _collection.SearchAsync(vector, limit, cancellationToken: cancellationToken);

        await foreach (var hit in hits.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // The cluster already ranked these, so the order it reported is the order the model sees.
            chunks.Add(new KnowledgeChunk
            {
                DocumentId = hit.Record.Path,
                Text = hit.Record.Text,
                Score = hit.Score ?? hit.Record.Distance,
            });
        }

        return chunks;
    }
}

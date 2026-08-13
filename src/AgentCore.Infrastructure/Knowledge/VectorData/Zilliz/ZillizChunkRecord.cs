namespace AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;

/// <summary>
/// One row the <c>kb_chunks</c> collection returns: a leaf path, the passage, and its distance.
/// </summary>
/// <remarks>
/// <para>
/// D7 is why the path is here. Path 2 says <c>search_chunks</c> ranks in the vector store and
/// returns a leaf path, and <c>knowledge.read</c> then opens that path in the file store. The row
/// therefore carries the path of the document rather than an opaque vector-store id: one field, and
/// the two halves of the knowledge base agree on what a document is called.
/// </para>
/// <para>
/// The chunk text rides along so a ranked passage costs no second call. The vector itself never
/// comes back: <c>outputFields</c> asks for the path and the text only.
/// </para>
/// </remarks>
public sealed record ZillizChunkRecord
{
    /// <summary>Gets the leaf path of the document the passage comes from.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the passage.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the distance the search reported. A larger number ranks first.</summary>
    /// <remarks>
    /// Milvus names this <c>distance</c> and Microsoft.Extensions.VectorData names it a score, so
    /// one number arrives twice: here, and on the <c>VectorSearchResult</c> that wraps this row.
    /// </remarks>
    public double Distance { get; init; }
}

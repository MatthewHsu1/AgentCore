using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// A collection built the ways a real one goes wrong.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KbShapedCorpus"/> and <see cref="ForeignCorpus"/> are both well-formed: different
/// naming, same discipline. Neither can show what happens when a collection does not carry what
/// the document claims it carries, and that is the case an operator actually meets — an ingester
/// renamed a field, or writes a body as chunks, or never built the text index.
/// </para>
/// <para>
/// Every failure these provoke must be loud AT STARTUP. A knowledge base that silently answers
/// with empty cards is worse than one that refuses to open, because nothing downstream can tell
/// the difference between "no answer" and "no knowledge base".
/// </para>
/// </remarks>
public static class HostileCorpus
{
    /// <summary>The vector width.</summary>
    public const int Dim = 8;

    /// <summary>The vector every query embeds to.</summary>
    public static float[] QueryVector()
    {
        var v = new float[Dim];
        v[0] = 1f;
        return v;
    }

    /// <summary>Points keyed by NUMBER, not by uuid. No uuid5 or direct lookup can ever address one.</summary>
    public static Task NumericKeysAsync(QdrantClient client, string collection, CancellationToken cancellationToken)
        => BuildAsync(client, collection, point =>
        {
            point.Id = new PointId { Num = 17 };
            point.Payload["doc_id"] = "DOC-01";
            point.Payload["content"] = "a numerically keyed point";
            point.Payload["related"] = List("DOC-99");
        }, cancellationToken);

    /// <summary>A body written as a LIST of chunks rather than one string.</summary>
    public static Task ChunkedBodyAsync(QdrantClient client, string collection, CancellationToken cancellationToken)
        => BuildAsync(client, collection, point =>
        {
            point.Id = new PointId { Uuid = Guid.NewGuid().ToString() };
            point.Payload["doc_id"] = "DOC-01";
            point.Payload["content"] = List("first chunk", "second chunk");
        }, cancellationToken);

    /// <summary>A well-formed point that simply does not carry the citation roles.</summary>
    public static Task NoCitationFieldsAsync(QdrantClient client, string collection, CancellationToken cancellationToken)
        => BuildAsync(client, collection, point =>
        {
            point.Id = new PointId { Uuid = Guid.NewGuid().ToString() };
            point.Payload["doc_id"] = "DOC-01";
            point.Payload["content"] = "a point with no origin and no page";
        }, cancellationToken);

    /// <summary>An authority written as TEXT where the document maps a trust rank.</summary>
    public static Task TextAuthorityAsync(QdrantClient client, string collection, CancellationToken cancellationToken)
        => BuildAsync(client, collection, point =>
        {
            point.Id = new PointId { Uuid = Guid.NewGuid().ToString() };
            point.Payload["doc_id"] = "DOC-01";
            point.Payload["content"] = "a point whose trust is a word";
            point.Payload["trust"] = "high";
        }, cancellationToken);

    private static Value List(params string[] values)
        => new() { ListValue = new ListValue { Values = { values.Select(v => new Value { StringValue = v }) } } };

    private static async Task BuildAsync(
        QdrantClient client, string collection, Action<PointStruct> fill, CancellationToken cancellationToken)
    {
        await client.CreateCollectionAsync(
            collection,
            new VectorParams { Size = Dim, Distance = Distance.Cosine },
            cancellationToken: cancellationToken);

        var point = new PointStruct { Vectors = QueryVector() };
        fill(point);

        await client.UpsertAsync(collection, [point], cancellationToken: cancellationToken);
    }
}

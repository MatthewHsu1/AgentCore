using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// A collection that shares no name with the one <c>kb sync</c> writes.
/// </summary>
/// <remarks>
/// Different vector name, flat top-level facets, a different id field, random point keys, and a
/// links field pointing at ids that no formula can derive. Every earlier test renamed one thing at
/// a time against the synthetic corpus, so each could still have passed on a store that read a
/// default somewhere; this one cannot.
/// </remarks>
public static class ForeignCorpus
{
    /// <summary>The vector width.</summary>
    public const int Dim = 8;

    /// <summary>How many documents the corpus holds.</summary>
    public const int Count = 6;

    /// <summary>The vector every query embeds to. Document 0 is nearest.</summary>
    public static float[] QueryVector()
    {
        var v = new float[Dim];
        v[0] = 1f;
        return v;
    }

    /// <summary>The id of document <paramref name="index"/>.</summary>
    public static string Id(int index) => $"DOC-{index:00}";

    /// <summary>Creates the collection and fills it. Point keys are random, so no formula derives them.</summary>
    public static async Task CreateAsync(
        QdrantClient client, string collection, CancellationToken cancellationToken)
    {
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["embedding"] = new VectorParams { Size = Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: cancellationToken);

        await client.CreatePayloadIndexAsync(
            collection, "doc_id", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);
        await client.CreatePayloadIndexAsync(
            collection, "content", PayloadSchemaType.Text, cancellationToken: cancellationToken);
        await client.CreatePayloadIndexAsync(
            collection, "region", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);

        var points = new List<PointStruct>(Count);

        for (var i = 0; i < Count; i++)
        {
            var angle = i * 0.2;
            var vector = new float[Dim];
            vector[0] = (float)Math.Cos(angle);
            vector[1] = (float)Math.Sin(angle);

            var point = new PointStruct
            {
                // Random, unrelated to doc_id. Only links.lookup: filter can find these.
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = new Vectors
                {
                    Vectors_ = new NamedVectors { Vectors = { ["embedding"] = vector } },
                },
            };

            point.Payload["doc_id"] = Id(i);
            point.Payload["content"] = $"document {Id(i)} about warranty returns and shipping";
            point.Payload["origin"] = $"handbook-{i}";
            point.Payload["page"] = $"s.{i}";
            point.Payload["trust"] = 2;
            point.Payload["region"] = i < 3 ? "emea" : "amer";

            // Document 0, the nearest, links to the last one -- which is in the other region, so a
            // scoped search must not show it.
            point.Payload["related"] = new Value
            {
                ListValue = new ListValue
                {
                    Values = { i == 0 ? [new Value { StringValue = Id(Count - 1) }] : [] },
                },
            };

            points.Add(point);
        }

        await client.UpsertAsync(collection, points, cancellationToken: cancellationToken);
    }
}

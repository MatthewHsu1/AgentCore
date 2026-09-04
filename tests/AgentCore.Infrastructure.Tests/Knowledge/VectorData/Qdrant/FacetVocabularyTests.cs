using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// The 30-card synthetic corpus, built once for the class. Every test here only reads.
/// </summary>
public sealed class FacetVocabularyCorpusFixture : IAsyncLifetime
{
    public string Name { get; } = $"facet-vocab-{Guid.NewGuid():N}";

    public QdrantClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!QdrantServer.IsConfigured)
        {
            return;
        }

        Client = QdrantServer.CreateClient();
        await KbShapedCorpus.CreateAsync(Client, Name, interleaved: true, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is null)
        {
            return;
        }

        try
        {
            await Client.DeleteCollectionAsync(Name);
        }
        finally
        {
            Client.Dispose();
        }
    }
}

/// <summary>
/// <see cref="QdrantKnowledgeStore.ReadAsync"/> against a real Qdrant: what §12's four acceptance
/// clauses ask of <see cref="IFacetVocabularyPort"/>.
/// </summary>
[Collection(QdrantServerCollection.Name)]
public sealed class FacetVocabularyTests : IClassFixture<FacetVocabularyCorpusFixture>
{
    private readonly FacetVocabularyCorpusFixture _corpus;

    public FacetVocabularyTests(FacetVocabularyCorpusFixture corpus) => _corpus = corpus;

    [QdrantFact]
    public async Task ReadAsync_ReturnsEveryDistinctValueAtThePath()
    {
        var values = await Port().ReadAsync("facets.model", 100, TestContext.Current.CancellationToken);

        Assert.Equal(["ct900", "ct900ent", "ctsbs900"], values.Order(StringComparer.Ordinal));
    }

    [QdrantFact]
    public async Task ReadAsync_ResultCountEqualsLimit_ReturnsExactlyLimitValues()
    {
        // card_id is a Keyword-indexed field carrying 30 distinct values -- far above this limit.
        var values = await Port().ReadAsync("card_id", 5, TestContext.Current.CancellationToken);

        Assert.Equal(5, values.Count);
    }

    [QdrantFact]
    public async Task ReadAsync_PathWithNoKeywordIndex_ThrowsAndNamesThePath()
    {
        // "body" carries no payload index at all in this corpus.
        var ex = await Assert.ThrowsAsync<RpcException>(
            async () => await Port().ReadAsync("body", 100, TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("body", ex.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task ReadAsync_WildcardValueComesBackAsAnOrdinaryValue()
    {
        var client = QdrantServer.CreateClient();
        var collection = $"facet-vocab-wildcard-{Guid.NewGuid():N}";

        try
        {
            await client.CreateCollectionAsync(
                collection,
                vectorsConfig: new VectorParamsMap
                {
                    Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
                },
                cancellationToken: TestContext.Current.CancellationToken);
            await client.CreatePayloadIndexAsync(
                collection,
                "facets.brand",
                PayloadSchemaType.Keyword,
                cancellationToken: TestContext.Current.CancellationToken);

            var points = new[]
            {
                WildcardCard("card-1", brand: "*"),
                WildcardCard("card-2", brand: "sole"),
            };
            await client.UpsertAsync(collection, points, cancellationToken: TestContext.Current.CancellationToken);

            var port = new QdrantKnowledgeStore(
                new QdrantSearchChannel(client),
                new FakeEmbeddingGenerator(KbShapedCorpus.QueryVector()),
                new QdrantKnowledgeStoreOptions
                {
                    Collection = collection,
                    Scoped = false,
                    Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
                });

            var values = await port.ReadAsync("facets.brand", 100, TestContext.Current.CancellationToken);

            Assert.Contains("*", values);
            Assert.Contains("sole", values);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
            client.Dispose();
        }
    }

    private static PointStruct WildcardCard(string cardId, string brand)
    {
        var point = new PointStruct
        {
            Id = new PointId { Uuid = Guid.NewGuid().ToString() },
            Vectors = new Vectors
            {
                Vectors_ = new NamedVectors { Vectors = { ["dense"] = KbShapedCorpus.QueryVector() } },
            },
        };

        point.Payload["card_id"] = cardId;
        point.Payload["body"] = $"{cardId} body";
        point.Payload["facets"] = new Value
        {
            StructValue = new Struct
            {
                Fields = { ["brand"] = new Value { StringValue = brand } },
            },
        };

        return point;
    }

    private QdrantKnowledgeStore Port() => new(
        new QdrantSearchChannel(_corpus.Client),
        new FakeEmbeddingGenerator(KbShapedCorpus.QueryVector()),
        new QdrantKnowledgeStoreOptions
        {
            Collection = _corpus.Name,
            Scoped = false,
            VectorName = "dense",
            Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
        });
}

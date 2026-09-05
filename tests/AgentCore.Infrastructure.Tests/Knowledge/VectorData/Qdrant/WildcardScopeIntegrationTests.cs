using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;
using static AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.TestScopes;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// A three-point collection shaped like the spec's wildcard-scope walkthrough: a company-wide
/// card, a brand-wide card, and a card scoped to one machine.
/// </summary>
/// <remarks>
/// Every test here only reads, so one collection serves them all, built once for the class.
/// </remarks>
public sealed class WildcardScopeCorpusFixture : IAsyncLifetime
{
    /// <summary>The named dense vector every point and every query use.</summary>
    public const string VectorName = "dense";

    private const int Dim = 4;

    /// <summary>The query vector, close to every point so none is dropped by the score floor.</summary>
    public static float[] QueryVector() => [1f, 0f, 0f, 0f];

    /// <summary>Gets the collection name. Dropped when the class finishes.</summary>
    public string Name { get; } = $"wildcard-scope-{Guid.NewGuid():N}";

    /// <summary>Gets the client the tests query through.</summary>
    public QdrantClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!QdrantServer.IsConfigured)
        {
            return;
        }

        Client = QdrantServer.CreateClient();

        await Client.CreateCollectionAsync(
            Name,
            vectorsConfig: new VectorParamsMap
            {
                Map = { [VectorName] = new VectorParams { Size = Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        await Client.CreatePayloadIndexAsync(
            Name, "card_id", PayloadSchemaType.Keyword, cancellationToken: TestContext.Current.CancellationToken);
        await Client.CreatePayloadIndexAsync(
            Name, "facets.brand", PayloadSchemaType.Keyword, cancellationToken: TestContext.Current.CancellationToken);
        await Client.CreatePayloadIndexAsync(
            Name, "facets.applies_to", PayloadSchemaType.Keyword, cancellationToken: TestContext.Current.CancellationToken);

        var points = new[]
        {
            Point("policy", brand: "*", appliesTo: "*"),
            Point("sole-care", brand: "sole", appliesTo: "*"),
            Point("f63-belt", brand: "sole", appliesTo: "f63"),
        };

        await Client.UpsertAsync(Name, points, cancellationToken: TestContext.Current.CancellationToken);
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

    /// <summary>
    /// One point, with <c>facets.brand</c> and <c>facets.applies_to</c> as single-element ARRAYS --
    /// the shape the real knowledge bank writes, and the shape <c>QdrantKnowledgeStore.Holds</c>
    /// matches a keyword against.
    /// </summary>
    private static PointStruct Point(string cardId, string brand, string appliesTo)
    {
        var point = new PointStruct
        {
            Id = new PointId { Uuid = Guid.NewGuid().ToString() },
            Vectors = new Vectors { Vectors_ = new NamedVectors { Vectors = { [VectorName] = QueryVector() } } },
        };

        point.Payload["card_id"] = cardId;
        point.Payload["body"] = $"{cardId} belt maintenance text";
        point.Payload["facets"] = new Value
        {
            StructValue = new Struct
            {
                Fields =
                {
                    ["brand"] = new Value
                    {
                        ListValue = new ListValue { Values = { new Value { StringValue = brand } } },
                    },
                    ["applies_to"] = new Value
                    {
                        ListValue = new ListValue { Values = { new Value { StringValue = appliesTo } } },
                    },
                },
            },
        };

        return point;
    }
}

/// <summary>
/// Proves the scope wildcard end to end against a real Qdrant: which card ids come back at each
/// step, never how many. The result is capped at <c>_options.Limit</c>, so a size assertion would
/// pin that cap rather than the scope.
/// </summary>
[Collection(QdrantServerCollection.Name)]
public sealed class WildcardScopeIntegrationTests : IClassFixture<WildcardScopeCorpusFixture>
{
    private readonly WildcardScopeCorpusFixture _corpus;

    public WildcardScopeIntegrationTests(WildcardScopeCorpusFixture corpus) => _corpus = corpus;

    [QdrantFact]
    public async Task Search_NothingKnown_ReturnsOnlyTheCompanyWideCard()
    {
        using (KnowledgeScopeScope.Open(Scope(("brand", "*"), ("applies_to", "*"))))
        {
            var cards = await Store.SearchAsync("belt", TestContext.Current.CancellationToken);
            Assert.Equal(["policy"], cards.Select(c => c.CardId).Order(StringComparer.Ordinal));
        }
    }

    [QdrantFact]
    public async Task Search_BrandOnly_AdmitsBrandWideAndRefusesTheMachineCard()
    {
        using (KnowledgeScopeScope.Open(Scope(("brand", "sole"), ("applies_to", "*"))))
        {
            var cards = await Store.SearchAsync("belt", TestContext.Current.CancellationToken);
            var ids = cards.Select(c => c.CardId).ToList();

            Assert.Contains("sole-care", ids);
            Assert.Contains("policy", ids);
            Assert.DoesNotContain("f63-belt", ids);
        }
    }

    [QdrantFact]
    public async Task Search_BrandAndMachine_AdmitsAllThree()
    {
        using (KnowledgeScopeScope.Open(Scope(("brand", "sole"), ("applies_to", "f63"))))
        {
            var cards = await Store.SearchAsync("belt", TestContext.Current.CancellationToken);
            var ids = cards.Select(c => c.CardId).ToList();

            Assert.Contains("f63-belt", ids);
            Assert.Contains("sole-care", ids);
            Assert.Contains("policy", ids);
        }
    }

    private QdrantKnowledgeStore Store => new(
        new QdrantSearchChannel(_corpus.Client),
        new FakeEmbeddingGenerator(WildcardScopeCorpusFixture.QueryVector()),
        new QdrantKnowledgeStoreOptions
        {
            Collection = _corpus.Name,
            Scoped = true,
            VectorName = WildcardScopeCorpusFixture.VectorName,
            Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
            ScopeTemplate = "facets.{key}",
            ScopeWildcard = "*",
            ScopeWildcardFacets = ["brand", "applies_to"],
            ScoreFloor = 0.0,
        });
}

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using Qdrant.Client;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

public sealed class ForeignCorpusFixture : IAsyncLifetime
{
    public string Name { get; } = $"foreign-{Guid.NewGuid():N}";

    public QdrantClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!QdrantServer.IsConfigured)
        {
            return;
        }

        Client = QdrantServer.CreateClient();
        await ForeignCorpus.CreateAsync(Client, Name, TestContext.Current.CancellationToken);
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
/// The whole path over a collection that shares no name with the one <c>kb sync</c> writes.
/// </summary>
[Collection(QdrantServerCollection.Name)]
public sealed class ForeignSchemaTests : IClassFixture<ForeignCorpusFixture>
{
    private readonly ForeignCorpusFixture _corpus;

    public ForeignSchemaTests(ForeignCorpusFixture corpus) => _corpus = corpus;

    [QdrantFact]
    public async Task Adapter_StartsAgainstAForeignCollection()
    {
        var store = await Adapter().CreateSearchAsync(
            Entry(), secrets: null, Embedder(), requireScope: false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(store);
        (store as IDisposable)?.Dispose();
    }

    [QdrantFact]
    public async Task Search_MapsEveryRenamedField()
    {
        var card = (await Store().SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken))[0];

        Assert.StartsWith("DOC-", card.CardId, StringComparison.Ordinal);
        Assert.Contains("warranty", card.Text, StringComparison.Ordinal);
        Assert.StartsWith("handbook-", card.SourceRef, StringComparison.Ordinal);
        Assert.StartsWith("s.", card.SourceLocator, StringComparison.Ordinal);
        Assert.Equal(2, card.Authority);
    }

    [QdrantFact]
    public async Task Search_FollowsLinksByFilterAcrossRandomPointKeys()
    {
        var cards = await Store().SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken);

        var linked = Assert.Single(cards, card => card.ViaLink);
        Assert.Equal(ForeignCorpus.Id(ForeignCorpus.Count - 1), linked.CardId);
    }

    [QdrantFact]
    public async Task Search_TopLevelFacetScopeFiltersAndReChecksLinks()
    {
        // Document 0 is emea and links to the last document, which is amer. The scope must keep the
        // ranked emea documents and drop the linked amer one.
        using var _ = KnowledgeScopeScope.Open(new KnowledgeScope
        {
            Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "emea" },
        });

        var cards = await Store(scoped: true).SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
        Assert.All(cards, card => Assert.False(card.ViaLink));
    }

    private static KnowledgeProviderConfiguration BaseEntry() => new()
    {
        Kind = QdrantKnowledgeAdapter.ProviderKind,
        Vector = "embedding",
        Fields = new KnowledgeFieldsConfiguration
        {
            Id = "doc_id",
            Body = "content",
            Lexical = "content",
            Source = "origin",
            Locator = "page",
            Authority = "trust",
        },
        Scope = new KnowledgeScopeConfiguration { Template = "{key}" },
        Links = new KnowledgeLinksConfiguration
        {
            Field = "related",
            Lookup = KnowledgeLinkLookup.Filter,
        },
        Analyzer = NoQueryAnalyzer.AnalyzerName,
    };

    private KnowledgeProviderConfiguration Entry() => BaseEntry() with { Collection = _corpus.Name };

    private static QdrantKnowledgeAdapter Adapter() => new(_ => QdrantServer.CreateClient());

    private static FakeEmbeddingGenerator Embedder() => new(ForeignCorpus.QueryVector());

    private QdrantKnowledgeStore Store(bool scoped = false)
    {
        var entry = Entry();

        return new QdrantKnowledgeStore(
            new QdrantSearchChannel(_corpus.Client),
            Embedder(),
            new QdrantKnowledgeStoreOptions
            {
                Collection = entry.Collection,
                Scoped = scoped,
                VectorName = entry.Vector,
                Fields = entry.Fields,
                ScopeTemplate = entry.Scope.Template,
                Links = entry.Links,
                Analyzer = new NoQueryAnalyzer(),
                Limit = 3,
                ScoreFloor = 0.0,
            });
    }
}

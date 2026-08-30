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
/// The whole path over a collection that shares no name with the synthetic corpus.
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

    [QdrantFact]
    public async Task Search_Limit_IsHonouredOnAForeignCollection()
    {
        // Limit, floor and fusion are the same code for every collection, but "the same code" is a
        // claim about the store, and the store reads its keys off this document. Proving them only
        // against the kb-shaped corpus leaves the naming and the ranking entangled.
        var cards = await Store(limit: 2).SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken);

        Assert.Equal(2, cards.Count(card => !card.ViaLink));
    }

    [QdrantFact]
    public async Task Search_ScoreFloor_DropsEverythingBelowIt()
    {
        var all = await Store(limit: 10, floor: 0.0).SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken);
        var floored = await Store(limit: 10, floor: 0.25).SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken);

        Assert.All(floored.Where(card => card.Score is not null), card => Assert.True(card.Score >= 0.25));
        Assert.True(floored.Count <= all.Count);
    }

    [QdrantFact]
    public async Task Search_NoLinksBlock_NeverExpands()
    {
        var cards = await Store(links: false).SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
        Assert.All(cards, card => Assert.False(card.ViaLink));
    }

    [QdrantFact]
    public async Task Search_TheIdRoleUnmapped_UsesThePointKey()
    {
        // The foreign corpus keys its points randomly, so a card id that is a GUID proves the store
        // fell back to the key rather than to a field name of its own.
        //
        // Links go with it: every lookup mode resolves a linked id through fields.id, so links plus
        // an unmapped id is a contradiction the store now refuses outright.
        var entry = Entry() with { Fields = BaseEntry().Fields! with { Id = null }, Links = null };

        var card = (await StoreFrom(entry).SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken))[0];

        Assert.True(Guid.TryParse(card.CardId, out _));
    }

    [QdrantFact]
    public async Task Search_TheLexicalRoleUnmapped_StillRanksByVector()
    {
        var entry = Entry() with { Fields = BaseEntry().Fields! with { Lexical = null } };

        var cards = await StoreFrom(entry).SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
    }

    [QdrantFact]
    public async Task Search_TheCitationRolesUnmapped_LeaveThemEmpty()
    {
        var entry = Entry() with
        {
            Fields = BaseEntry().Fields! with { Source = null, Locator = null, Authority = null },
        };

        var card = (await StoreFrom(entry).SearchAsync(
            "warranty returns", TestContext.Current.CancellationToken))[0];

        Assert.Equal(string.Empty, card.SourceRef);
        Assert.Equal(string.Empty, card.SourceLocator);
        Assert.Null(card.Authority);
    }

    [QdrantFact]
    public void Store_LinksWithNoIdMapped_IsRefusedOutright()
    {
        var entry = Entry() with { Fields = BaseEntry().Fields! with { Id = null } };

        var thrown = Assert.Throws<ArgumentException>(() => StoreFrom(entry));

        Assert.Contains("fields.id", thrown.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task Search_ScopedWithNoAmbient_Throws()
    {
        var store = Store(scoped: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.SearchAsync("warranty returns", TestContext.Current.CancellationToken));
    }

    private static KnowledgeProviderConfiguration BaseEntry() => new()
    {
        Kind = QdrantKnowledgeAdapter.ProviderKind,
        Collection = "replaced-per-fixture",
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

    private QdrantKnowledgeStore Store(
        bool scoped = false, int limit = 3, double floor = 0.0, bool links = true)
    {
        var entry = Entry();

        return StoreFrom(links ? entry : entry with { Links = null }, scoped, limit, floor);
    }

    private QdrantKnowledgeStore StoreFrom(
        KnowledgeProviderConfiguration entry, bool scoped = false, int limit = 3, double floor = 0.0)
        => new(
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
                Limit = limit,
                ScoreFloor = floor,
            });
}

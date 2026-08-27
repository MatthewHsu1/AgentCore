using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// One interleaved corpus, built once for the whole class.
/// </summary>
/// <remarks>
/// Every test here only reads, so one collection serves them all. Building one per test made
/// <c>CreatePayloadIndexAsync</c> exceed the client's 30 s deadline against a scratch container.
/// </remarks>
public sealed class SyntheticCorpusFixture : IAsyncLifetime
{
    /// <summary>Gets the collection name. Dropped when the class finishes.</summary>
    public string Name { get; } = $"store-{Guid.NewGuid():N}";

    /// <summary>Gets the client the tests query through.</summary>
    public QdrantClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!QdrantServer.IsConfigured)
        {
            return;
        }

        Client = QdrantServer.CreateClient();
        await SyntheticCorpus.CreateAsync(
            Client, Name, interleaved: true, TestContext.Current.CancellationToken);
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

[Collection(QdrantServerCollection.Name)]
public sealed class QdrantKnowledgeStoreTests : IClassFixture<SyntheticCorpusFixture>
{
    private readonly SyntheticCorpusFixture _corpus;

    public QdrantKnowledgeStoreTests(SyntheticCorpusFixture corpus) => _corpus = corpus;

    [QdrantFact]
    public async Task SearchAsync_ScopedSearch_ReturnsOnlyThatProduct()
    {
        // Card 0 is in scope and links to card 29, which is not. So this also holds up the scope
        // re-check on see_also expansion: a key lookup carries no filter of its own.
        using var _ = Ct900();

        var cards = await LinkedStore().SearchAsync(SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
        var models = await ModelsOf(cards);
        Assert.All(models, model => Assert.Equal("ct900", model));
    }

    [QdrantFact]
    public async Task SearchAsync_Unscoped_WouldMixProducts()
    {
        // The negative control for the test above. Without it, a dropped scope filter is invisible.
        // `scoped: false` is the design's own switch for a whole-corpus read. An empty facet map is
        // not that switch, and now fails closed like an absent ambient.
        var cards = await Store(scoped: false).SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        Assert.True((await ModelsOf(cards)).Distinct(StringComparer.Ordinal).Count() > 1);
    }

    [QdrantFact]
    public async Task SearchAsync_ScopedStoreWithNoAmbient_Throws()
    {
        // A21, first door. An absent scope fails closed. It never searches every customer's cards.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Store().SearchAsync(SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken));

        Assert.Contains("no KnowledgeScope is open", thrown.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task SearchAsync_ScopedStoreWithEmptyFacets_Throws()
    {
        // A21, second door. A host that reads a customer record with no product on it builds this
        // scope without noticing, and an empty facet map filters nothing. Same leak as no ambient
        // at all, so the same refusal -- but a different message, because it is a different bug.
        using var _ = KnowledgeScopeScope.Open(
            new KnowledgeScope { Facets = new Dictionary<string, string>(StringComparer.Ordinal) });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Store().SearchAsync(SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken));

        Assert.Contains("names no facets", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_LeftAtTheirDefaults_CarryTheValuesTheDesignNames()
    {
        // Every other test names a limit and a floor, so a typo in either default ships silently.
        var options = new QdrantKnowledgeStoreOptions { Collection = "anything", Scoped = false };

        // 0.25 is exact in float32. At 0.1 the value round-trips to 0.10000000149011612 and the
        // floor's `>=` cannot be told from `>`.
        Assert.Equal(0.25, options.ScoreFloor);
        Assert.Equal(5, options.Limit);
        // Left unset, VectorName means the collection's single anonymous vector -- not a name `kb
        // sync` owns, so unlike Fields.Lexical it has no Application-side constant to fall back to.
        Assert.Null(options.VectorName);
        Assert.Equal("text", options.Fields.Lexical, StringComparer.Ordinal);
    }

    [QdrantFact]
    public async Task SearchAsync_LookalikeIdentifier_RanksTheIdentifierCardFirst()
    {
        // A22. Card 7 holds e33 and sits one dense rank BELOW card 6. Only the required
        // prefetch lifts it.
        var cards = await Store(scoped: false).SearchAsync(
            SyntheticCorpus.LookalikeQuery, TestContext.Current.CancellationToken);

        Assert.Equal(SyntheticCorpus.Id(7), cards[0].CardId);
    }

    [QdrantFact]
    public async Task SearchAsync_TwoIdentifiersThatCoOccurNowhere_LeavesTheDenseAnswerStanding()
    {
        // A22's real proof. Under `must` the leg matches nothing and the dense order stands.
        // Under `should` it would match both cards and reorder. `must` and `should` are the
        // SAME filter for one token, so no single-identifier test can tell them apart.
        var cards = await Store(scoped: false).SearchAsync(
            SyntheticCorpus.TwoIdentifierQuery, TestContext.Current.CancellationToken);

        Assert.Equal(SyntheticCorpus.Id(0), cards[0].CardId);
    }

    [QdrantFact]
    public async Task SearchAsync_LimitAboveTheDefaultPrefetchDepth_ReturnsThatMany()
    {
        // A27's recall ceiling. The limit is deliberately above both Qdrant's own default prefetch
        // depth (10) and the depth a hardcoded constant would use (20), so either one caps the
        // result below what the caller asked for.
        var cards = await Store(limit: 25, scoped: false).SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        Assert.True(
            cards.Count(card => !card.ViaLink) >= 25,
            $"expected at least 25 ranked cards, got {cards.Count(card => !card.ViaLink)}");
    }

    [QdrantFact]
    public async Task SearchAsync_ScoreFloor_KeepsExactlyTheCardsAtOrAboveIt()
    {
        // The floor must be exact in float32, or `>=` cannot be told from `>`.
        var all = await Store(floor: 0.0, limit: 20, scoped: false).SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);
        var floored = await Store(floor: 0.25, limit: 20, scoped: false).SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        var atOrAbove = all.Count(card => card.Score >= 0.25);

        // The boundary is only testable while some card scores exactly 0.25 and some card scores
        // less. Without both, `>=` and `>` agree and the comparison below proves nothing.
        Assert.Contains(all, card => card.Score == 0.25);
        Assert.Contains(all, card => card.Score < 0.25);
        Assert.Equal(atOrAbove, floored.Count(card => !card.ViaLink));
    }

    [QdrantFact]
    public async Task SearchAsync_TopHitHasSeeAlso_PullsTheLinkedCardIn()
    {
        // A7. Card 0's only link is card 29, which is the FARTHEST card, so it can never be
        // on the page already. Nothing is scoped here, so `InScope` is vacuously true and the link
        // mechanism itself is what the assertion sees.
        var cards = await LinkedStore(limit: 3, scoped: false).SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        var linked = Assert.Single(cards, card => card.ViaLink);
        Assert.Equal(SyntheticCorpus.Id(SyntheticCorpus.Count - 1), linked.CardId);
        Assert.Null(linked.Score);
    }

    [QdrantFact]
    public async Task SearchAsync_ScopedOnAListFacetThatHolds_KeepsTheLinkedCard()
    {
        // Holds' list branch, which had no test at all while the corpus carried only scalar facets --
        // and applies_to is a real array facet in the sibling knowledge-bank design, so a deployment
        // scoped on it would have dropped every linked card with no error. Every card carries
        // SharedAudience, so this scope holds for card 29 too and the link survives the re-check.
        using var _ = KnowledgeScopeScope.Open(new KnowledgeScope
        {
            Facets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["applies_to"] = SyntheticCorpus.SharedAudience,
            },
        });

        var cards = await LinkedStore(limit: 3, floor: 0.0).SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        var linked = Assert.Single(cards, card => card.ViaLink);
        Assert.Equal(SyntheticCorpus.Id(SyntheticCorpus.Count - 1), linked.CardId);
    }

    [QdrantFact]
    public async Task SearchAsync_ScopedOnAListFacetThatDoesNot_DropsTheLinkedCard()
    {
        // The other half of the same branch. Card 0 is ct900 and links to card 29, which is ct900ent,
        // so card 29's applies_to list does NOT hold this scope. Without this fact a list branch that
        // simply answered true would pass the test above.
        using var _ = KnowledgeScopeScope.Open(new KnowledgeScope
        {
            Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["applies_to"] = "ct900" },
        });

        var cards = await LinkedStore(limit: 3, floor: 0.0).SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
        Assert.DoesNotContain(cards, card => card.ViaLink);
    }

    [QdrantFact]
    public async Task SearchAsync_MapsTheNestedPayload()
    {
        using var _ = Ct900();

        var card = (await Store().SearchAsync(SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken))[0];

        Assert.StartsWith("manifest-", card.SourceRef, StringComparison.Ordinal);
        Assert.Equal("p.1", card.SourceLocator);
        Assert.NotNull(card.Authority);
        Assert.InRange(card.Authority.Value, 1, 3);
        Assert.NotEmpty(card.Text);
        Assert.False(card.ViaLink);
    }

    [Fact]
    public async Task SearchAsync_ChannelHangsPastTheDeadline_Cancels()
    {
        // The one test a real server cannot serve. This is why the seam exists.
        var store = new QdrantKnowledgeStore(
            new HangingSearchChannel(TimeSpan.FromSeconds(30)),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = "anything",
                VectorName = "dense",
                Scoped = false,
                Deadline = TimeSpan.FromMilliseconds(20),
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.SearchAsync("anything", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAsync_CallerCancels_Cancels()
    {
        // The other one: the caller's token must be linked in, not replaced by the deadline.
        var store = new QdrantKnowledgeStore(
            new HangingSearchChannel(TimeSpan.FromSeconds(30)),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = "anything",
                VectorName = "dense",
                Scoped = false,

                // Longer than the channel hangs for, so nothing but the caller's own token can end
                // this call. At the default 10 s the store's deadline ends it instead and the test
                // passes whether or not the caller's token was ever linked in.
                Deadline = TimeSpan.FromMinutes(5),
            });

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.SearchAsync("anything", caller.Token));
    }

    private static IDisposable Ct900() => KnowledgeScopeScope.Open(
        new KnowledgeScope { Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["model"] = "ct900" } });

    private QdrantKnowledgeStore Store(int limit = 10, double floor = 0.0, bool scoped = true)
        => new(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Limit = limit,
                ScoreFloor = floor,
                Scoped = scoped,
            });

    // SyntheticCorpus keys every point uuid5, and Store() carries no Links now that the feature is
    // opt-in -- so a test that exercises see_also expansion needs this instead.
    private QdrantKnowledgeStore LinkedStore(int limit = 10, double floor = 0.0, bool scoped = true)
        => new(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Limit = limit,
                ScoreFloor = floor,
                Scoped = scoped,
                Links = new KnowledgeLinksConfiguration { Lookup = KnowledgeLinkLookup.Uuid5 },
            });

    private async Task<List<string>> ModelsOf(IReadOnlyList<KnowledgeCard> cards)
    {
        var points = await _corpus.Client.RetrieveAsync(
            _corpus.Name,
            [.. cards.Select(card => new PointId { Uuid = KbPointId.For(card.CardId).ToString() })],
            withPayload: true,
            cancellationToken: TestContext.Current.CancellationToken);

        return [.. points.Select(point => point.Payload["facets"].StructValue.Fields["model"].StringValue)];
    }

    [QdrantFact]
    public async Task SearchAsync_RenamedFields_ReadsThemAndIgnoresTheDefaults()
    {
        // The synthetic corpus writes BOTH `text` and `body` with the same string, so pointing the
        // body field at `text` proves the option is read without needing a second corpus. A store
        // that ignored Fields.Body would still return non-empty text and pass by accident, so the
        // assertion below is on a field the corpus gives a DIFFERENT value: the citation.
        using var _ = Ct900();

        var store = new QdrantKnowledgeStore(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Scoped = true,
                Limit = 10,
                ScoreFloor = 0.0,
                Fields = new KnowledgeFieldsConfiguration { Source = "card_id", Locator = "text" },
            });

        var card = (await store.SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken))[0];

        Assert.Equal(card.CardId, card.SourceRef);
        Assert.NotEmpty(card.SourceLocator);
        Assert.DoesNotContain("manifest-", card.SourceRef, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task SearchAsync_IdRoleDisabled_UsesThePointKeyAsTheCardId()
    {
        // Audit falls back to the point key; SyntheticCorpus keys every point uuid5, so the id
        // must parse as a GUID when fields.id is null.
        var store = new QdrantKnowledgeStore(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Scoped = false,
                Limit = 3,
                ScoreFloor = 0.0,
                Fields = new KnowledgeFieldsConfiguration { Id = null },
            });

        var card = (await store.SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken))[0];

        Assert.True(Guid.TryParse(card.CardId, out _), $"'{card.CardId}' is not a point key");
        Assert.NotEmpty(card.Text);
    }

    [QdrantFact]
    public async Task SearchAsync_LexicalRoleDisabled_StillAnswersAnIdentifierQuery()
    {
        // The supported spelling of "no full-text index": no required-term leg is built, so the
        // lookalike lift disappears but the dense leg still answers.
        var store = new QdrantKnowledgeStore(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Scoped = false,
                Limit = 10,
                ScoreFloor = 0.0,
                Fields = new KnowledgeFieldsConfiguration { Lexical = null },
            });

        var cards = await store.SearchAsync(
            SyntheticCorpus.TwoIdentifierQuery, TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
    }

    [QdrantFact]
    public async Task SearchAsync_NoQueryAnalyzer_StillAnswersTheLookalikeQuery()
    {
        // With no required-term leg the identifier card is no longer lifted, so this asserts only
        // that the single-leg path works at all -- not that ranking is unchanged.
        using var _ = Ct900();

        var store = new QdrantKnowledgeStore(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Scoped = true,
                Limit = 10,
                ScoreFloor = 0.0,
                Analyzer = new NoQueryAnalyzer(),
            });

        var cards = await store.SearchAsync(
            SyntheticCorpus.LookalikeQuery, TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
    }

    [QdrantTheory]
    [InlineData(KnowledgeLinkLookup.Uuid5)]
    [InlineData(KnowledgeLinkLookup.Filter)]
    public async Task SearchAsync_EveryLookupMode_FollowsTheSameLink(KnowledgeLinkLookup lookup)
    {
        // Card 0 is nearest the query vector and links to card 29, the farthest -- so a linked card
        // on the page cannot have arrived by ranking. Both modes must find the same one.
        var store = new QdrantKnowledgeStore(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Scoped = false,
                Limit = 3,
                ScoreFloor = 0.0,
                Links = new KnowledgeLinksConfiguration { Lookup = lookup },
            });

        var cards = await store.SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        var linked = Assert.Single(cards, card => card.ViaLink);
        Assert.Equal(SyntheticCorpus.Id(SyntheticCorpus.Count - 1), linked.CardId);
        Assert.Null(linked.Score);
    }

    [QdrantFact]
    public async Task SearchAsync_FilterLookupWithScopeOpen_StillDropsOutOfScopeLinks()
    {
        // The scope re-check must run on the scroll path too. Card 0 is ct900 and links to card 29,
        // which the interleaved corpus makes ct900ent -- so the link must NOT come back.
        using var _ = Ct900();

        var store = new QdrantKnowledgeStore(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Scoped = true,
                Limit = 3,
                ScoreFloor = 0.0,
                Links = new KnowledgeLinksConfiguration { Lookup = KnowledgeLinkLookup.Filter },
            });

        var cards = await store.SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        Assert.All(cards, card => Assert.False(card.ViaLink));
    }

    [QdrantFact]
    public async Task SearchAsync_NoLinksBlock_NeverExpands()
    {
        // Card 0's payload still says see_also: [syn-29]; with no links: block that is data, not behaviour.
        var cards = await Store(limit: 3, scoped: false).SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
        Assert.All(cards, card => Assert.False(card.ViaLink));
    }

    [QdrantFact]
    public async Task SearchAsync_NoLinksFieldInPayload_ReturnsRankedCardsOnly()
    {
        var store = new QdrantKnowledgeStore(
            new QdrantSearchChannel(_corpus.Client),
            new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
            new QdrantKnowledgeStoreOptions
            {
                Collection = _corpus.Name,
                VectorName = "dense",
                Scoped = false,
                Limit = 3,
                ScoreFloor = 0.0,
                Links = new KnowledgeLinksConfiguration { Field = "no_such_field" },
            });

        var cards = await store.SearchAsync(
            SyntheticCorpus.PlainQuery, TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
        Assert.All(cards, card => Assert.False(card.ViaLink));
    }

    [QdrantFact]
    public async Task SearchAsync_AnonymousVectorCollection_RanksWithoutAUsingClause()
    {
        var collection = $"anonstore-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            new VectorParams { Size = SyntheticCorpus.Dim, Distance = Distance.Cosine },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = SyntheticCorpus.QueryVector(),
            };
            point.Payload["card_id"] = "anon-00";
            point.Payload["body"] = "anonymous vector card";
            await client.UpsertAsync(collection, [point], cancellationToken: TestContext.Current.CancellationToken);

            var store = new QdrantKnowledgeStore(
                new QdrantSearchChannel(QdrantServer.CreateClient()),
                new FakeEmbeddingGenerator(SyntheticCorpus.QueryVector()),
                new QdrantKnowledgeStoreOptions
                {
                    Collection = collection,
                    Scoped = false,
                    Limit = 3,
                    ScoreFloor = 0.0,
                    Analyzer = new NoQueryAnalyzer(),
                });

            using var _ = (IDisposable)store;
            var card = Assert.Single(await store.SearchAsync("anonymous", TestContext.Current.CancellationToken));
            Assert.Equal("anonymous vector card", card.Text);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }
}

using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

[Collection(QdrantServerCollection.Name)]
public sealed class SyntheticCorpusTests
{
    [QdrantFact]
    public async Task CreateAsync_BuildsANamedVectorCollectionWithNestedFacets()
    {
        using var client = QdrantServer.CreateClient();
        var collection = $"synthetic-{Guid.NewGuid():N}";

        try
        {
            await SyntheticCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);

            var info = await client.GetCollectionInfoAsync(collection, TestContext.Current.CancellationToken);
            Assert.True(info.Config.Params.VectorsConfig.ParamsMap.Map.ContainsKey("dense"));
            Assert.Equal(
                (ulong)SyntheticCorpus.Count,
                await client.CountAsync(collection, cancellationToken: TestContext.Current.CancellationToken));

            // The facet must be a real nested path, not a flat key with a dot in it.
            var filtered = await client.ScrollAsync(
                collection,
                filter: MatchKeyword("facets.model", "ct900"),
                limit: 100,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEmpty(filtered.Result);

            // The flat shape must find nothing. This is the negative control that would have
            // caught A25 before it was written.
            var flat = await client.ScrollAsync(
                collection,
                filter: MatchKeyword("facets_model", "ct900"),
                limit: 100,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(flat.Result);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task SearchAsync_AgainstTheNamedDenseVector_ReturnsCardsInIndexOrder()
    {
        using var client = QdrantServer.CreateClient();
        var collection = $"synthetic-{Guid.NewGuid():N}";

        try
        {
            await SyntheticCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);

            var results = await client.QueryAsync(
                collection,
                query: SyntheticCorpus.QueryVector(),
                usingVector: "dense",
                limit: (ulong)SyntheticCorpus.Count,
                payloadSelector: true,
                cancellationToken: TestContext.Current.CancellationToken);

            // A wrong distance function, a wrong vector name, or a broken upsert would all still
            // return 30 points -- only checking the actual order proves the server round-trip.
            var order = results.Select(r => r.Payload["card_id"].StringValue).ToList();
            var expected = Enumerable.Range(0, SyntheticCorpus.Count).Select(SyntheticCorpus.Id).ToList();
            Assert.Equal(expected, order);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Cards_DenseSimilarityToQuery_StrictlyDecreasesWithIndex()
    {
        var query = SyntheticCorpus.QueryVector();
        var cards = SyntheticCorpus.Cards(interleaved: true);
        var similarities = cards.Select(c => DotProduct(c.Vector, query)).ToList();

        for (var i = 1; i < similarities.Count; i++)
        {
            Assert.True(
                similarities[i] < similarities[i - 1],
                $"card {i} must be strictly farther from the query than card {i - 1}, or dense rank stops equalling card index");
        }
    }

    [Fact]
    public void LookalikeIdentifierCard_RanksExactlyOneBelowItsRival()
    {
        var cards = SyntheticCorpus.Cards(interleaved: true);
        var query = SyntheticCorpus.QueryVector();

        var e27 = cards.Single(c => c.CardId == SyntheticCorpus.Id(6));
        var e33 = cards.Single(c => c.CardId == SyntheticCorpus.Id(7));

        Assert.Contains("e27", e27.Text, StringComparison.Ordinal);
        Assert.Contains("e33", e33.Text, StringComparison.Ordinal);

        // Only a required-identifier prefetch can lift e33 above e27 on the look-alike query --
        // that is the entire point Task 8 tests. If this drifts, that test proves nothing.
        Assert.True(DotProduct(e27.Vector, query) > DotProduct(e33.Vector, query));
    }

    [Fact]
    public void Cards_Interleaved_SpreadsOutOfScopeCardsThroughTheRanking()
    {
        var maxRun = LongestRunOfInScopeCards(SyntheticCorpus.Cards(interleaved: true));

        // A single early stray followed by 27 in-scope cards would still satisfy "some out-of-scope
        // card ranks above the last in-scope card" -- that is a boundary crossing, not dispersion.
        // Bounding the longest unbroken run of in-scope cards anywhere in the ranking is what a
        // dropped scope filter cannot hide behind: any long run means the nearest cards for a
        // scope-filtered query are all in scope anyway, so the filter changes nothing observable.
        Assert.True(
            maxRun <= SyntheticCorpus.Count / 5,
            $"the longest run of consecutive in-scope cards was {maxRun}; out-of-scope cards must be spread through the ranking, not merely present somewhere above the tail");
    }

    [Fact]
    public void Cards_NotInterleaved_ClustersOutOfScopeCardsAtTheTail()
    {
        // The negative control: interleaved:false is the trap a fixture author walks into without
        // noticing, proven here (by the same metric as the positive case) so the `true` case above
        // is known to test something real rather than merely "not this exact other arrangement".
        var maxRun = LongestRunOfInScopeCards(SyntheticCorpus.Cards(interleaved: false));

        Assert.True(
            maxRun > SyntheticCorpus.Count / 2,
            $"the longest run of consecutive in-scope cards was only {maxRun}; interleaved:false should genuinely cluster out-of-scope cards at the tail");
    }

    [Fact]
    public void Card0SeeAlso_PointsToTheFarthestCard()
    {
        var cards = SyntheticCorpus.Cards(interleaved: true);

        Assert.Equal([SyntheticCorpus.Id(SyntheticCorpus.Count - 1)], cards[0].SeeAlso);
        Assert.All(cards.Skip(1), c => Assert.Empty(c.SeeAlso));
    }

    private static int LongestRunOfInScopeCards(IReadOnlyList<SyntheticCard> cards)
    {
        var longest = 0;
        var current = 0;

        foreach (var card in cards)
        {
            current = card.Model == "ct900" ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static float DotProduct(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static Filter MatchKeyword(string key, string value)
    {
        var filter = new Filter();
        filter.Must.Add(new Condition { Field = new FieldCondition { Key = key, Match = new Match { Keyword = value } } });
        return filter;
    }
}

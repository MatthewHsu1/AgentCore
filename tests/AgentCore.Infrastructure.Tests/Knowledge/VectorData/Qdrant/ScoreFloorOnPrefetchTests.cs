using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// <c>scoreFloor</c> is a cosine similarity, and Qdrant applies it on the dense prefetch. It is never
/// compared against a fused score: with one leg, RRF hands back <c>1/(rank+1)</c> for every query, so a
/// floor on that number is a fixed rank cut — measured 2026-09-02 on the owner's collection as exactly
/// three cards for every question, answered or not.
/// </summary>
public sealed class ScoreFloorOnPrefetchTests
{
    /// <summary>What a single-leg RRF fusion returns for any five points, whatever was asked.</summary>
    private static readonly float[] RankScores = [0.5f, 1f / 3f, 0.25f, 0.2f, 1f / 6f];

    [Fact]
    public async Task SearchAsync_PutsTheFloorOnEveryDensePrefetch()
    {
        var channel = new CapturingSearchChannel(Points(RankScores));

        await Store(channel, floor: 0.35).SearchAsync("belt", TestContext.Current.CancellationToken);

        var leg = Assert.Single(channel.Query!.Prefetch);
        Assert.True(leg.HasScoreThreshold, "the dense prefetch carries no score_threshold");
        Assert.Equal(0.35f, leg.ScoreThreshold);
    }

    [Fact]
    public async Task SearchAsync_WithZeroFloor_EmitsNoScoreThreshold()
    {
        var channel = new CapturingSearchChannel(Points(RankScores));

        await Store(channel, floor: 0.0).SearchAsync("belt", TestContext.Current.CancellationToken);

        var leg = Assert.Single(channel.Query!.Prefetch);
        Assert.False(leg.HasScoreThreshold, "a floor of 0 means no floor, so no score_threshold should ride the prefetch");
    }

    [Fact]
    public async Task SearchAsync_DoesNotCutTheReturnedPointsByScore()
    {
        var channel = new CapturingSearchChannel(Points(RankScores));

        var cards = await Store(channel, floor: 0.35).SearchAsync("belt", TestContext.Current.CancellationToken);

        // Qdrant already applied the floor before these came back. Cutting again by a fused score
        // would keep one of five here, and three of five at 0.25.
        Assert.Equal(5, cards.Count);
    }

    [Fact]
    public async Task SearchAsync_WithOneLeg_AsksForTheNearestVectorRatherThanAFusion()
    {
        var channel = new CapturingSearchChannel(Points(RankScores));

        await Store(channel, floor: 0.35).SearchAsync("belt", TestContext.Current.CancellationToken);

        // One leg has nothing to fuse. A nearest query over the prefetch re-scores the same vector,
        // so a card's score is its cosine similarity and the floor and the score share a scale.
        Assert.Equal(Query.VariantOneofCase.Nearest, channel.Query!.Query.VariantCase);
    }

    [Fact]
    public async Task SearchAsync_WithOneLegOverAnAnonymousVector_NamesNoVectorAnywhere()
    {
        var channel = new CapturingSearchChannel(Points(RankScores));

        await Store(channel, floor: 0.35).SearchAsync("belt", TestContext.Current.CancellationToken);

        Assert.Null(channel.Query!.Using);
        Assert.Equal(string.Empty, Assert.Single(channel.Query.Prefetch).Using);
    }

    [Fact]
    public async Task SearchAsync_WithOneLegOverANamedVector_NamesItOnBothTheLegAndTheQuery()
    {
        var channel = new CapturingSearchChannel(Points(RankScores));

        await Store(channel, floor: 0.35, vector: "dense").SearchAsync("belt", TestContext.Current.CancellationToken);

        Assert.Equal("dense", channel.Query!.Using);
        Assert.Equal("dense", Assert.Single(channel.Query.Prefetch).Using);
    }

    [Fact]
    public async Task SearchAsync_WithABlankVectorName_ReadsItAsTheAnonymousVector()
    {
        // The leg and the top-level query have to agree on which collection shape they are querying,
        // and a blank name is the same shape as no name at all.
        var channel = new CapturingSearchChannel(Points(RankScores));

        await Store(channel, floor: 0.35, vector: string.Empty).SearchAsync("belt", TestContext.Current.CancellationToken);

        Assert.Null(channel.Query!.Using);
        Assert.Equal(string.Empty, Assert.Single(channel.Query.Prefetch).Using);
    }

    private static IReadOnlyList<ScoredPoint> Points(float[] scores) =>
        [.. scores.Select((score, i) =>
        {
            var point = new ScoredPoint { Id = new PointId { Uuid = Guid.NewGuid().ToString() }, Score = score };
            point.Payload["card_id"] = $"card-{i}";
            point.Payload["body"] = "text";
            return point;
        })];

    private static QdrantKnowledgeStore Store(
        CapturingSearchChannel channel, double floor, string? vector = null) => new(
        channel,
        new FakeEmbeddingGenerator([1f]),
        new QdrantKnowledgeStoreOptions
        {
            Collection = "anything",
            Scoped = false,
            Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
            ScoreFloor = floor,
            VectorName = vector,
        });
}

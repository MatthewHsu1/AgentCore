using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;

using Qdrant.Client.Grpc;

using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// Reading whole cards by one exact facet value, which is what a model year needs: a keyword match,
/// never a similarity.
/// </summary>
/// <remarks>
/// Two adjacent model years read as near-identical to an embedding, so a ranked search for one
/// returns the other. A filtered scroll cannot make that mistake: a card carries the value or it
/// does not.
/// </remarks>
public sealed class FacetReadTests
{
    [Fact]
    public async Task ReadByFacetAsync_ReturnsTheCardsTheScrollAnswered()
    {
        var channel = new FacetScrollChannel(Card("lcr-2023-model-overview"), Card("lcr-2023-belt-tension"));

        var cards = await Store(channel).ReadByFacetAsync(
            "facets.model", "lcr-2023", 50, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["lcr-2023-belt-tension", "lcr-2023-model-overview"],
            cards.Select(card => card.CardId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadByFacetAsync_FiltersOnThePathVerbatim()
    {
        var channel = new FacetScrollChannel(Card("lcr-2023-model-overview"));

        await Store(channel).ReadByFacetAsync(
            "facets.model", "lcr-2023", 50, TestContext.Current.CancellationToken);

        var condition = Assert.Single(channel.LastFilter!.Must).Field;
        Assert.Equal("facets.model", condition.Key);
        Assert.Equal("lcr-2023", condition.Match.Keyword);
    }

    [Fact]
    public async Task ReadByFacetAsync_PassesTheLimitThrough()
    {
        var channel = new FacetScrollChannel(Card("lcr-2023-model-overview"));

        await Store(channel).ReadByFacetAsync(
            "facets.model", "lcr-2023", 7, TestContext.Current.CancellationToken);

        Assert.Equal(7u, channel.LastLimit);
    }

    [Fact]
    public async Task ReadByFacetAsync_AnUnknownValueIsEmptyAndNotAnError()
    {
        var cards = await Store(new FacetScrollChannel()).ReadByFacetAsync(
            "facets.model", "nope-1999", 50, TestContext.Current.CancellationToken);

        Assert.Empty(cards);
    }

    [Fact]
    public async Task ReadByFacetAsync_NeverRanks()
    {
        // FacetScrollChannel throws on every method but ScrollAsync. Reaching this assertion is the
        // proof that no vector was built and no embedding was generated.
        var cards = await Store(new FacetScrollChannel(Card("lcr-2023-model-overview"))).ReadByFacetAsync(
            "facets.model", "lcr-2023", 50, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(cards).Score);
    }

    [Fact]
    public async Task ReadByFacetAsync_RejectsANegativeLimit()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await Store(new FacetScrollChannel()).ReadByFacetAsync(
                "facets.model", "lcr-2023", -1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheStoreServesTheFacetReadAsACapability()
    {
        // What a caller asks before it reads. The store answers for itself here; a wrapper would
        // forward the same question inward.
        IKnowledgeRetrievalPort store = Store(new FacetScrollChannel());

        Assert.Same(store, store.GetService<IKnowledgeFacetReadPort>());
    }

    private static RetrievedPoint Card(string cardId)
    {
        var point = new RetrievedPoint { Id = new PointId { Uuid = Guid.NewGuid().ToString() } };
        point.Payload["card_id"] = cardId;
        point.Payload["body"] = $"{cardId} body";
        return point;
    }

    private static QdrantKnowledgeStore Store(IQdrantSearchChannel channel) => new(
        channel,
        new FakeEmbeddingGenerator([1f, 0f, 0f, 0f]),
        new QdrantKnowledgeStoreOptions
        {
            Collection = "kb",
            Scoped = false,
            Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
        });
}

using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

[Collection(QdrantServerCollection.Name)]
public sealed class QdrantSearchChannelTests
{
    [QdrantFact]
    public async Task QueryAsync_ReturnsPointsInFusedOrder()
    {
        using var client = QdrantServer.CreateClient();
        var collection = $"channel-{Guid.NewGuid():N}";

        try
        {
            await KbShapedCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);
            var channel = new QdrantSearchChannel(client);

            var prefetch = new PrefetchQuery
            {
                Limit = 20,
                Using = "dense",
                Query = new Query
                {
                    Nearest = new VectorInput
                    {
                        Dense = new DenseVector { Data = { KbShapedCorpus.QueryVector() } },
                    },
                },
            };

            var points = await channel.QueryAsync(
                new SearchQuery(collection, [prefetch], new Query { Fusion = Fusion.Rrf }, 5),
                TestContext.Current.CancellationToken);

            Assert.Equal(5, points.Count);
            Assert.Equal(KbShapedCorpus.Id(0), points[0].Payload["card_id"].StringValue);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task RetrieveAsync_FetchesTheWholePayloadByKey()
    {
        using var client = QdrantServer.CreateClient();
        var collection = $"channel-{Guid.NewGuid():N}";

        try
        {
            await KbShapedCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);
            var channel = new QdrantSearchChannel(client);

            var points = await channel.RetrieveAsync(
                collection,
                [KbShapedCorpus.PointKey(KbShapedCorpus.Id(3)), KbShapedCorpus.PointKey(KbShapedCorpus.Id(9))],
                TestContext.Current.CancellationToken);

            Assert.Equal(2, points.Count);
            Assert.Contains(points, p => p.Payload["card_id"].StringValue == KbShapedCorpus.Id(3));
            Assert.True(points[0].Payload.ContainsKey("see_also"));
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task QueryAsync_CancelledToken_Throws()
    {
        using var client = QdrantServer.CreateClient();
        var channel = new QdrantSearchChannel(client);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await channel.QueryAsync(
            new SearchQuery("anything", [], new Query { Fusion = Fusion.Rrf }, 1), cancelled.Token));
    }
}

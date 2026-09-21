using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant
{
    /// <summary>
    /// <c>links.lookup: direct</c> against a fake channel.
    /// </summary>
    /// <remarks>
    /// No corpus anywhere in this plan keys its points by the card id itself: the synthetic corpus
    /// uses <c>uuid5</c> keys and the foreign corpus uses random ones, so a live server never runs this
    /// branch. These two drive it through <see cref="RecordingSearchChannel"/> instead, needing no
    /// Qdrant server at all.
    /// </remarks>
    public sealed class DirectLinkLookupTests
    {
        [Fact]
        public async Task SearchAsync_DirectLookupWithNonGuidCardId_ThrowsNamingTheId()
        {
            const string linkedId = "not-a-guid";
            RecordingSearchChannel channel = new([Point("card-1", linkedId)]);
            QdrantKnowledgeStore store = Store(channel);

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await store.SearchAsync("anything", null, TestContext.Current.CancellationToken));

            Assert.Contains(linkedId, thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchAsync_DirectLookupWithGuidShapedCardId_ResolvesToThatGuid()
        {
            Guid linkedId = Guid.NewGuid();
            RecordingSearchChannel channel = new([Point("card-1", linkedId.ToString())]);
            QdrantKnowledgeStore store = Store(channel);

            _ = await store.SearchAsync("anything", null, TestContext.Current.CancellationToken);

            IReadOnlyList<Guid> retrieved = Assert.Single(channel.RetrievedIds);
            Assert.Equal(linkedId, Assert.Single(retrieved));
        }

        /// <summary>One scored point whose own id is <paramref name="cardId"/> and whose only link is <paramref name="linkedId"/>.</summary>
        private static ScoredPoint Point(string cardId, string linkedId)
        {
            ScoredPoint point = new() { Id = new PointId { Uuid = Guid.NewGuid().ToString() }, Score = 1f };
            point.Payload["card_id"] = cardId;
            point.Payload["body"] = "text";
            point.Payload["see_also"] = new Value
            {
                ListValue = new ListValue { Values = { new Value { StringValue = linkedId } } },
            };
            return point;
        }

        private static QdrantKnowledgeStore Store(RecordingSearchChannel channel)
        {
            return new(
            channel,
            new FakeEmbeddingGenerator([1f]),
            new QdrantKnowledgeStoreOptions
            {
                Collection = "anything",
                Scoped = false,
                Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
                Links = new KnowledgeLinksConfiguration
                {
                    Field = "see_also",
                    Lookup = KnowledgeLinkLookup.Direct,
                },
                ScoreFloor = 0.0,
            });
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Tools;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Zilliz;

/// <summary>
/// The ranking port over the Zilliz connector.
/// </summary>
/// <remarks>
/// Path 2 of D7: <c>search_chunks</c> ranks in the vector store and returns a leaf path, and
/// <c>knowledge.read</c> opens that path in the file store. This class embeds the query, searches,
/// and turns each hit into one <c>KnowledgeChunk</c>, so the path is what the model reads back.
/// </remarks>
public sealed class ZillizRetrievalStoreTests
{
    private const string TwoHits =
        """
        {
          "code": 0,
          "data": [
            { "distance": 0.82, "path": "policies/shipping.md", "text": "We ship in two days." },
            { "distance": 0.41, "path": "policies/returns.md",  "text": "Returns take a week." }
          ]
        }
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItEmbedsTheQueryAndReturnsTheLeafPathOfEveryHit()
    {
        List<string> bodies = [];
        using var handler = Answering(HttpStatusCode.OK, TwoHits, bodies);
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://cluster.test", UriKind.Absolute) };
        using ZillizCollection collection = new(client, "kb_chunks", "zilliz-test-not-a-real-key");
        FakeEmbeddingGenerator embeddings = new(0.5f, 0.25f);
        ZillizRetrievalStore store = new(collection, embeddings);

        var chunks = await store.SearchAsync("how long is shipping", 2, Token);

        Assert.Equal(["how long is shipping"], embeddings.Inputs);
        Assert.Equal(2, chunks.Count);
        Assert.Equal("policies/shipping.md", chunks[0].DocumentId);
        Assert.Equal("We ship in two days.", chunks[0].Text);
        Assert.Equal(0.82, chunks[0].Score, 6);
        Assert.Equal("policies/returns.md", chunks[1].DocumentId);
        Assert.Equal(0.41, chunks[1].Score, 6);

        // The embedded query is what the cluster searched by.
        using var body = JsonDocument.Parse(Assert.Single(bodies));
        Assert.Equal(
            [0.5f, 0.25f],
            body.RootElement.GetProperty("data")[0].EnumerateArray().Select(value => value.GetSingle()));
        Assert.Equal(2, body.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ALimitThatAsksForNothingCostsNoEmbeddingAndNoRequest()
    {
        List<string> bodies = [];
        using var handler = Answering(HttpStatusCode.OK, TwoHits, bodies);
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://cluster.test", UriKind.Absolute) };
        using ZillizCollection collection = new(client, "kb_chunks", "zilliz-test-not-a-real-key");
        FakeEmbeddingGenerator embeddings = new(0.5f);
        ZillizRetrievalStore store = new(collection, embeddings);

        var chunks = await store.SearchAsync("anything", 0, Token);

        Assert.Empty(chunks);
        Assert.Empty(embeddings.Inputs);
        Assert.Empty(handler.Requests);
    }

    private static StubHttpMessageHandler Answering(HttpStatusCode status, string body, List<string> bodies)
        => new(request =>
        {
            bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });
}

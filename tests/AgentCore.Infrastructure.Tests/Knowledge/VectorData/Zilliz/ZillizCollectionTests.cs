using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;
using AgentCore.Infrastructure.Tests.Tools;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Zilliz;

/// <summary>
/// The Milvus v2 REST connector behind the <c>zilliz</c> vendor.
/// </summary>
/// <remarks>
/// <para>
/// D14 gives the connector the vector store and nothing else, and only <c>SearchAsync</c> has a
/// caller today. Every other member throws, and the sweep at the end of this file is item 3a of
/// section 11: it names each one, so the day <c>index-sync</c> implements one, the sweep says so.
/// </para>
/// <para>
/// Every test answers from a fake handler, so nothing here opens a socket.
/// </para>
/// </remarks>
public sealed class ZillizCollectionTests
{
    private const string ApiKey = "zilliz-test-not-a-real-key";
    private const string CollectionName = "kb_chunks";

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
    public async Task ItPostsTheSearchTheMilvusRestApiExpects()
    {
        using FakeCluster cluster = new(HttpStatusCode.OK, TwoHits);

        await ReadAsync(cluster.Collection.SearchAsync(new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]), 5, cancellationToken: Token));

        var request = Assert.Single(cluster.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v2/vectordb/entities/search", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer " + ApiKey, request.Headers.Authorization!.ToString());

        using var body = JsonDocument.Parse(Assert.Single(cluster.Bodies));
        var root = body.RootElement;
        Assert.Equal(CollectionName, root.GetProperty("collectionName").GetString());
        Assert.Equal("vector", root.GetProperty("annsField").GetString());
        Assert.Equal(5, root.GetProperty("limit").GetInt32());
        Assert.Equal(
            ["path", "text"],
            root.GetProperty("outputFields").EnumerateArray().Select(field => field.GetString()));

        // One query is one row of the data matrix, and that row is the query vector.
        var data = root.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal(
            [0.1f, 0.2f, 0.3f],
            data[0].EnumerateArray().Select(value => value.GetSingle()));
    }

    [Fact]
    public async Task ItMapsEveryHitInTheOrderTheClusterReportsThem()
    {
        using FakeCluster cluster = new(HttpStatusCode.OK, TwoHits);

        var hits = await ReadAsync(cluster.Collection.SearchAsync(new ReadOnlyMemory<float>([1f]), 2, cancellationToken: Token));

        Assert.Equal(2, hits.Count);
        Assert.Equal("policies/shipping.md", hits[0].Record.Path);
        Assert.Equal("We ship in two days.", hits[0].Record.Text);
        Assert.Equal(0.82, hits[0].Record.Distance, 6);
        Assert.Equal(0.82, hits[0].Score!.Value, 6);
        Assert.Equal("policies/returns.md", hits[1].Record.Path);
        Assert.Equal(0.41, hits[1].Score!.Value, 6);
    }

    [Fact]
    public void ItReadsTheCollectionNameItWasOpenedOver()
    {
        using FakeCluster cluster = new(HttpStatusCode.OK, TwoHits);

        Assert.Equal(CollectionName, cluster.Collection.Name);
    }

    [Fact]
    public async Task ANonZeroMilvusCodeFailsAndNamesTheEndpointAndTheCode()
    {
        const string Denied = """{ "code": 1800, "message": "collection not found" }""";
        using FakeCluster cluster = new(HttpStatusCode.OK, Denied);

        var failure = await Assert.ThrowsAsync<VectorStoreException>(
            async () => await ReadAsync(cluster.Collection.SearchAsync(new ReadOnlyMemory<float>([1f]), 3, cancellationToken: Token)));

        Assert.Contains("/v2/vectordb/entities/search", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1800", failure.Message, StringComparison.Ordinal);
        Assert.Contains("collection not found", failure.Message, StringComparison.Ordinal);
        Assert.Equal(CollectionName, failure.CollectionName);
    }

    [Fact]
    public async Task AFailedRequestFailsAndNamesTheEndpointAndTheStatus()
    {
        using FakeCluster cluster = new(HttpStatusCode.InternalServerError, """{ "code": 0 }""");

        var failure = await Assert.ThrowsAsync<VectorStoreException>(
            async () => await ReadAsync(cluster.Collection.SearchAsync(new ReadOnlyMemory<float>([1f]), 3, cancellationToken: Token)));

        Assert.Contains("/v2/vectordb/entities/search", failure.Message, StringComparison.Ordinal);
        Assert.Contains("500", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyThatIsNotJsonFailsAndNamesTheEndpoint()
    {
        using FakeCluster cluster = new(HttpStatusCode.OK, "<html>gateway</html>");

        var failure = await Assert.ThrowsAsync<VectorStoreException>(
            async () => await ReadAsync(cluster.Collection.SearchAsync(new ReadOnlyMemory<float>([1f]), 3, cancellationToken: Token)));

        Assert.Contains("/v2/vectordb/entities/search", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyWithNoDataArrayFailsAndNamesTheEndpoint()
    {
        using FakeCluster cluster = new(HttpStatusCode.OK, """{ "code": 0 }""");

        var failure = await Assert.ThrowsAsync<VectorStoreException>(
            async () => await ReadAsync(cluster.Collection.SearchAsync(new ReadOnlyMemory<float>([1f]), 3, cancellationToken: Token)));

        Assert.Contains("/v2/vectordb/entities/search", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASearchByAnythingOtherThanAVectorIsNotSupported()
    {
        using FakeCluster cluster = new(HttpStatusCode.OK, TwoHits);

        // The store embeds the query. This connector holds no generator, so text is not an input.
        Assert.Throws<NotSupportedException>(() => cluster.Collection.SearchAsync("shipping", 3, cancellationToken: Token));
        Assert.Empty(cluster.Handler.Requests);
    }

    /// <summary>
    /// Item 3a of section 11: every member D14 assigns to the connector but no caller needs yet.
    /// </summary>
    /// <remarks>
    /// <c>EnsureCollectionExistsAsync</c>, <c>UpsertAsync</c>, and <c>DeleteAsync</c> belong to
    /// <c>index-sync</c>, and that work is out of scope here. Each one says so rather than opening a
    /// half-built write path.
    /// </remarks>
    [Fact]
    public async Task EveryMemberThisConnectorDoesNotImplementSaysSo()
    {
        using FakeCluster cluster = new(HttpStatusCode.OK, TwoHits);
        var collection = cluster.Collection;
        ZillizChunkRecord record = new() { Path = "policies/shipping.md", Text = "We ship in two days." };

        await Assert.ThrowsAsync<NotSupportedException>(async () => await collection.CollectionExistsAsync(Token));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await collection.EnsureCollectionExistsAsync(Token));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await collection.EnsureCollectionDeletedAsync(Token));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await collection.GetAsync("policies/shipping.md", cancellationToken: Token));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await collection.UpsertAsync(record, Token));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await collection.UpsertAsync([record], Token));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await collection.DeleteAsync("policies/shipping.md", Token));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await collection.DeleteAsync(["policies/shipping.md"], Token));

        Assert.Throws<NotSupportedException>(() => collection.GetAsync(["policies/shipping.md"], cancellationToken: Token));
        Assert.Throws<NotSupportedException>(() => collection.GetAsync(chunk => chunk.Path != null, 3, cancellationToken: Token));

        // Nothing above reached the cluster.
        Assert.Empty(cluster.Handler.Requests);
    }

    /// <summary>
    /// <c>GetService</c> is a probe, and it is outside the sweep above.
    /// </summary>
    /// <remarks>
    /// The package documents the answer as the object, otherwise null. A decorator that asks for a
    /// type this connector does not have must get null and not a crash, so the D14 refusal stops at
    /// the data plane and this member answers.
    /// </remarks>
    [Fact]
    public void ItAnswersAProbeRatherThanRefusingIt()
    {
        using FakeCluster cluster = new(HttpStatusCode.OK, TwoHits);
        var collection = cluster.Collection;

        Assert.Same(collection, collection.GetService(typeof(ZillizCollection)));
        Assert.Same(collection, collection.GetService(typeof(VectorStoreCollection<string, ZillizChunkRecord>)));

        // A type this connector does not have, and a keyed probe, both read as nothing found.
        Assert.Null(collection.GetService(typeof(VectorStoreCollectionMetadata)));
        Assert.Null(collection.GetService(typeof(ZillizCollection), "keyed"));

        Assert.Throws<ArgumentNullException>(() => collection.GetService(null!));
        Assert.Empty(cluster.Handler.Requests);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Drains a search, because the connector reads the cluster only while it is read.</summary>
    private static async Task<List<VectorSearchResult<ZillizChunkRecord>>> ReadAsync(
        IAsyncEnumerable<VectorSearchResult<ZillizChunkRecord>> hits)
    {
        List<VectorSearchResult<ZillizChunkRecord>> read = [];
        await foreach (var hit in hits.WithCancellation(Token))
        {
            read.Add(hit);
        }

        return read;
    }

    /// <summary>One collection over a handler that answers one scripted response.</summary>
    private sealed class FakeCluster : IDisposable
    {
        private readonly HttpClient _client;
        private readonly StubHttpMessageHandler _handler;

        public FakeCluster(HttpStatusCode status, string body)
        {
            _handler = new StubHttpMessageHandler(request =>
            {
                // The request content is disposed once the send returns, so the body is read here.
                Bodies.Add(request.Content is null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());

                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            });

            _client = new HttpClient(_handler)
            {
                BaseAddress = new Uri("https://in03-test.serverless.gcp-us-west1.cloud.zilliz.com", UriKind.Absolute),
            };

            Collection = new ZillizCollection(_client, CollectionName, ApiKey);
        }

        public ZillizCollection Collection { get; }

        public StubHttpMessageHandler Handler => _handler;

        /// <summary>Gets the body of every request, in call order.</summary>
        public List<string> Bodies { get; } = [];

        public void Dispose()
        {
            Collection.Dispose();
            _client.Dispose();
            _handler.Dispose();
        }
    }
}

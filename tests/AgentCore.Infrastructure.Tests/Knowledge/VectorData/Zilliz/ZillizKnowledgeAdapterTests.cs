using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;
using AgentCore.Infrastructure.Llm;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Zilliz;

/// <summary>
/// The <c>zilliz</c> vendor of the knowledge seam.
/// </summary>
/// <remarks>
/// The adapter owns the vendor only: the cluster URL, the key, the collection, and the embedding
/// model. It serves the ranking port alone, so a document that names <c>zilliz</c> for
/// <c>documents</c> is stopped by the composite before this class is asked for anything.
/// </remarks>
public sealed class ZillizKnowledgeAdapterTests
{
    private const string ApiKey = "zilliz-test-not-a-real-key";
    private const string Endpoint = "https://in03-test.serverless.gcp-us-west1.cloud.zilliz.com";

    private const string OneHit =
        """
        { "code": 0, "data": [ { "distance": 0.5, "path": "policies/shipping.md", "text": "Two days." } ] }
        """;

    /// <summary>A pipeline for the tests that read a property or fail before any request.</summary>
    private static readonly StubHandlerFactory Offline =
        new(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ItServesTheZillizKindAndTheRankingPortOnly()
    {
        ZillizKnowledgeAdapter adapter = new(Offline);

        Assert.Equal("zilliz", adapter.Kind);
        Assert.True(adapter.CanServeSearch);
        Assert.False(adapter.CanServeDocuments);
    }

    /// <summary>
    /// The pipeline is required, so no adapter of this vendor sends without the retry.
    /// </summary>
    /// <remarks>
    /// A default pipeline built here would carry no retry and no rate limit answer, and nothing would
    /// say so. This connector refuses a search option it cannot honour for the same reason.
    /// </remarks>
    [Fact]
    public void APipelineThatIsNothingIsRefusedWhereItIsBound()
        => Assert.Throws<ArgumentNullException>(() => new ZillizKnowledgeAdapter(null!));

    [Fact]
    public async Task TheDocumentHalfIsNotSupported()
    {
        ZillizKnowledgeAdapter adapter = new(Offline);
        KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };

        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await adapter.CreateDocumentsAsync(entry, new MapSecretResolver().With(
                ZillizKnowledgeAdapter.ApiKeySecretName, ApiKey), Token));
    }

    [Fact]
    public async Task NoEndpointFailsTheLoadAndPointsAtTheField()
    {
        ZillizKnowledgeAdapter adapter = new(Offline);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await adapter.CreateSearchAsync(new KnowledgeProviderConfiguration(), null, Token));

        Assert.Equal("/providers/knowledge/endpoint", failure.Pointer);
    }

    [Fact]
    public async Task AnEndpointThatIsNotAUrlFailsTheLoadAndPointsAtTheSameField()
    {
        ZillizKnowledgeAdapter adapter = new(Offline);
        KnowledgeProviderConfiguration entry = new() { Endpoint = "not a url" };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await adapter.CreateSearchAsync(entry, null, Token));

        Assert.Equal("/providers/knowledge/endpoint", failure.Pointer);
    }

    [Fact]
    public async Task ItOpensTheDefaultCollectionAndReachesNothingWhileItStarts()
    {
        List<string> bodies = [];
        using var handler = Answering(bodies);
        ZillizKnowledgeAdapter adapter = new(new StubHandlerFactory(handler), new FakeEmbeddingGenerator(0.5f));
        KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };
        MapSecretResolver resolver = new();
        resolver.With(ZillizKnowledgeAdapter.ApiKeySecretName, ApiKey);

        var search = await adapter.CreateSearchAsync(entry, resolver, Token);

        // Startup opens no socket. Only the first search does.
        Assert.Empty(handler.Requests);

        await search.SearchAsync("shipping", 3, Token);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Endpoint, request.RequestUri!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/v2/vectordb/entities/search", request.RequestUri.AbsolutePath);
        Assert.Equal("Bearer " + ApiKey, request.Headers.Authorization!.ToString());

        using var body = JsonDocument.Parse(Assert.Single(bodies));
        Assert.Equal(
            KnowledgeProviderConfiguration.DefaultCollection,
            body.RootElement.GetProperty("collectionName").GetString());
    }

    /// <summary>
    /// The adapter opens its client on the one pipeline the host built, by name.
    /// </summary>
    /// <remarks>
    /// That pipeline owns the connection lifetime, the retry, and the rate limit answer. This
    /// adapter owns the cluster URL, the key, and the collection, and no policy at all.
    /// </remarks>
    [Fact]
    public async Task ItOpensItsClientOnThePipelineTheHostBuilt()
    {
        List<string> bodies = [];
        using var handler = Answering(bodies);
        StubHandlerFactory pipeline = new(handler);
        ZillizKnowledgeAdapter adapter = new(pipeline, new FakeEmbeddingGenerator(0.5f));
        KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };
        MapSecretResolver resolver = new();
        resolver.With(ZillizKnowledgeAdapter.ApiKeySecretName, ApiKey);

        var search = await adapter.CreateSearchAsync(entry, resolver, Token);
        await search.SearchAsync("shipping", 3, Token);

        Assert.Equal([ZillizKnowledgeAdapter.HttpClientName], pipeline.Names);
        Assert.Equal("Bearer " + ApiKey, Assert.Single(handler.Requests).Headers.Authorization!.ToString());
    }

    /// <summary>
    /// A search that says nothing gives the call back, rather than holding it for the default 100 seconds.
    /// </summary>
    /// <remarks>
    /// The pipeline sets this deadline on the clients it hands out, and this adapter builds its own
    /// client over the handler chain, so the deadline is set here as well.
    /// </remarks>
    [Fact]
    public async Task ItGivesTheSearchClientADeadlineTheShippedDefaultDoesNotHave()
    {
        List<string> bodies = [];
        using var handler = Answering(bodies);
        ZillizKnowledgeAdapter adapter = new(new StubHandlerFactory(handler), new FakeEmbeddingGenerator(0.5f));
        KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };
        MapSecretResolver resolver = new();
        resolver.With(ZillizKnowledgeAdapter.ApiKeySecretName, ApiKey);

        var search = await adapter.CreateSearchAsync(entry, resolver, Token);

        var store = Assert.IsType<ZillizRetrievalStore>(search);
        var connector = Assert.IsType<ZillizCollection>(store.Collection);
        Assert.Equal(ZillizKnowledgeAdapter.SearchDeadline, connector.Deadline);
        Assert.True(ZillizKnowledgeAdapter.SearchDeadline < TimeSpan.FromSeconds(100));
    }

    [Fact]
    public async Task ItOpensTheCollectionTheDocumentNames()
    {
        List<string> bodies = [];
        using var handler = Answering(bodies);
        ZillizKnowledgeAdapter adapter = new(new StubHandlerFactory(handler), new FakeEmbeddingGenerator(0.5f));
        KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint, Collection = "support_chunks" };
        MapSecretResolver resolver = new();
        resolver.With(ZillizKnowledgeAdapter.ApiKeySecretName, ApiKey);

        var search = await adapter.CreateSearchAsync(entry, resolver, Token);
        await search.SearchAsync("shipping", 3, Token);

        using var body = JsonDocument.Parse(Assert.Single(bodies));
        Assert.Equal("support_chunks", body.RootElement.GetProperty("collectionName").GetString());
    }

    [Fact]
    public async Task TheApiKeyFallsBackToTheStandardVariable()
    {
        var saved = Environment.GetEnvironmentVariable(ZillizKnowledgeAdapter.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(ZillizKnowledgeAdapter.ApiKeyVariableName, ApiKey);

        try
        {
            List<string> bodies = [];
            using var handler = Answering(bodies);
            ZillizKnowledgeAdapter adapter = new(new StubHandlerFactory(handler), new FakeEmbeddingGenerator(0.5f));
            KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };
            MapSecretResolver resolver = new();

            var search = await adapter.CreateSearchAsync(entry, resolver, Token);
            await search.SearchAsync("shipping", 3, Token);

            // The chain is asked first, and the variable answers only when the chain holds nothing.
            Assert.Equal([ZillizKnowledgeAdapter.ApiKeySecretName], resolver.Asked);
            Assert.Equal("Bearer " + ApiKey, Assert.Single(handler.Requests).Headers.Authorization!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ZillizKnowledgeAdapter.ApiKeyVariableName, saved);
        }
    }

    [Fact]
    public async Task NoApiKeyAnywhereFailsAndSaysWhereToPutOne()
    {
        var saved = Environment.GetEnvironmentVariable(ZillizKnowledgeAdapter.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(ZillizKnowledgeAdapter.ApiKeyVariableName, null);

        try
        {
            List<string> bodies = [];
            using var handler = Answering(bodies);
            ZillizKnowledgeAdapter adapter = new(new StubHandlerFactory(handler), new FakeEmbeddingGenerator(0.5f));
            KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };

            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await adapter.CreateSearchAsync(entry, new MapSecretResolver(), Token));

            Assert.Contains(ZillizKnowledgeAdapter.ApiKeySecretName, failure.Message, StringComparison.Ordinal);
            Assert.Contains(ZillizKnowledgeAdapter.ApiKeyVariableName, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ZillizKnowledgeAdapter.ApiKeyVariableName, saved);
        }
    }

    /// <summary>
    /// The real embedding path: no generator is injected, so the adapter builds the OpenAI one.
    /// </summary>
    /// <remarks>
    /// Section 3.1 fixes the model and the width, and this reads both back off the generator the
    /// adapter built. Building an OpenAI client opens no socket, so this reaches no network.
    /// </remarks>
    [Fact]
    public async Task ItEmbedsWithTheModelAndTheWidthSectionThreePointOneNames()
    {
        List<string> bodies = [];
        using var handler = Answering(bodies);
        ZillizKnowledgeAdapter adapter = new(new StubHandlerFactory(handler));
        KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };
        MapSecretResolver resolver = new();
        resolver.With(ZillizKnowledgeAdapter.ApiKeySecretName, ApiKey);
        resolver.With(OpenAiChatClientAdapter.ApiKeySecretName, "sk-test-not-a-real-key");

        var search = await adapter.CreateSearchAsync(entry, resolver, Token);

        // The cluster key is read first, and the embedding key second.
        Assert.Equal(
            [ZillizKnowledgeAdapter.ApiKeySecretName, OpenAiChatClientAdapter.ApiKeySecretName],
            resolver.Asked);

        var store = Assert.IsType<ZillizRetrievalStore>(search);
        var metadata = Assert.IsType<EmbeddingGeneratorMetadata>(
            store.Embeddings.GetService(typeof(EmbeddingGeneratorMetadata)));

        Assert.Equal("text-embedding-3-small", ZillizKnowledgeAdapter.EmbeddingModel);
        Assert.Equal(1024, ZillizKnowledgeAdapter.EmbeddingDimensions);
        Assert.Equal(ZillizKnowledgeAdapter.EmbeddingModel, metadata.DefaultModelId);
        Assert.Equal(ZillizKnowledgeAdapter.EmbeddingDimensions, metadata.DefaultModelDimensions);

        // Building the generator reached nothing.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheOpenAiKeyFallsBackToTheStandardVariable()
    {
        var saved = Environment.GetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, "sk-test-not-a-real-key");

        try
        {
            List<string> bodies = [];
            using var handler = Answering(bodies);
            ZillizKnowledgeAdapter adapter = new(new StubHandlerFactory(handler));
            KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };
            MapSecretResolver resolver = new();
            resolver.With(ZillizKnowledgeAdapter.ApiKeySecretName, ApiKey);

            var search = await adapter.CreateSearchAsync(entry, resolver, Token);

            // The chain is asked for both names, and the variable answers the one it holds nothing for.
            Assert.Equal(
                [ZillizKnowledgeAdapter.ApiKeySecretName, OpenAiChatClientAdapter.ApiKeySecretName],
                resolver.Asked);
            Assert.IsType<ZillizRetrievalStore>(search);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, saved);
        }
    }

    /// <summary>
    /// A host with a cluster key and no OpenAI key cannot embed, and it learns that at startup.
    /// </summary>
    /// <remarks>
    /// The zilliz store embeds every query, so the OpenAI credential is not optional here. The
    /// failure arrives before any HTTP, which is what makes it a startup failure and not a call one.
    /// </remarks>
    [Fact]
    public async Task NoOpenAiKeyAnywhereFailsAndSaysWhereToPutOne()
    {
        var saved = Environment.GetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, null);

        try
        {
            List<string> bodies = [];
            using var handler = Answering(bodies);
            ZillizKnowledgeAdapter adapter = new(new StubHandlerFactory(handler));
            KnowledgeProviderConfiguration entry = new() { Endpoint = Endpoint };
            MapSecretResolver resolver = new();
            resolver.With(ZillizKnowledgeAdapter.ApiKeySecretName, ApiKey);

            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await adapter.CreateSearchAsync(entry, resolver, Token));

            Assert.Contains(OpenAiChatClientAdapter.ApiKeySecretName, failure.Message, StringComparison.Ordinal);
            Assert.Contains(OpenAiChatClientAdapter.ApiKeyVariableName, failure.Message, StringComparison.Ordinal);
            Assert.Equal(
                [ZillizKnowledgeAdapter.ApiKeySecretName, OpenAiChatClientAdapter.ApiKeySecretName],
                resolver.Asked);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, saved);
        }
    }

    private static StubHttpMessageHandler Answering(List<string> bodies)
        => new(request =>
        {
            bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OneHit, Encoding.UTF8, "application/json"),
            };
        });
}

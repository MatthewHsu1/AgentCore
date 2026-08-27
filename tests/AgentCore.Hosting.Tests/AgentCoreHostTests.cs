using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Transcript.Memory;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Transcript.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Hosting.Tests;

/// <summary>
/// The two calls a host makes, and the promises they carry.
/// </summary>
/// <remarks>
/// <para>
/// This library exists so a host owns no wiring, and the whole of that promise is that a host can
/// still overrule any part of it. Every test here is about who wins, because that is the one thing
/// the ordering inside <c>Configure</c> can get wrong without failing to compile: each <c>Use*</c>
/// seam is a setter, so defaults written after a host's callback would silently replace it.
/// </para>
/// <para>
/// Every test runs offline against a fake model. There is no OpenAI account, no network call, and
/// no API key anywhere in this file — which is itself the proof that the default vendor list costs
/// nothing until a document names it.
/// </para>
/// </remarks>
public sealed class AgentCoreHostTests
{
    /// <summary>A model that says one thing, which is all the compile table asks for.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>The factory behind every model reference in these tests.</summary>
    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        public IChatClient GetChatClient(ModelReference? model) => new FakeChatClient();
    }

    // ---------------------------------------------------------------------------------------------
    // Who wins. The host does, on every seam, because its callback runs last.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHostChatClientFactoryWinsOverTheDefaultVendor()
    {
        // The default list names the real OpenAI adapter. If it ran after the callback it would
        // replace this factory, and the start would then want a key this test does not set.
        using var host = await StartAsync();

        Assert.NotNull(host.Services.GetService<IChatClientFactory>());
    }

    [Fact]
    public async Task AHostThatHandsOverADocumentDoesNotAlsoGetTheDefaultPath()
    {
        // Both a path and a configuration is two documents, and AddAgentCoreAsync refuses that. The
        // default path has to stand down for a host that named a document of its own.
        using var host = await StartAsync();

        Assert.NotNull(host.Services.GetService<IChatClientFactory>());
    }

    // ---------------------------------------------------------------------------------------------
    // The durable seams. Naming a vendor here is what makes providers.audit.kind: postgres startable.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProvidersAuditPostgresReachesTheAdapterRatherThanTheSelector()
    {
        // The failure this guards is the selector's: a kind no registered adapter serves. Reaching
        // the credential instead is the proof that the default list names the PostgreSQL vendor.
        var failure = await Assert.ThrowsAsync<SecretResolutionException>(
            () => StartAsync(options => options.SecretResolver = new EmptySecretResolver(), Audit));

        Assert.Contains(KnownSecrets.PostgresConnectionString.Name, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvidersTranscriptPostgresReachesTheAdapterRatherThanTheSelector()
    {
        var failure = await Assert.ThrowsAsync<SecretResolutionException>(
            () => StartAsync(options => options.SecretResolver = new EmptySecretResolver(), Transcript));

        Assert.Contains(KnownSecrets.PostgresConnectionString.Name, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentNamingNoDurableVendorStillGetsTheInProcessStores()
    {
        // Naming the PostgreSQL vendor must not make it the default. The factory answers an absent
        // block before it reads the adapter list, and this is the line that holds it to that.
        using var host = await StartAsync();

        Assert.IsType<InMemoryAuditSink>(host.Services.GetRequiredService<QueuedAuditSink>().Store);
        Assert.IsType<InMemoryTranscriptStore>(host.Services.GetRequiredService<ITranscriptStore>());
    }

    [Fact]
    public async Task AHostAuditSinkWinsOverTheDefaultVendorOnTheSameKind()
    {
        // UseAuditSinks is a setter, so this replaces the list rather than joining it. Were the
        // default written after the callback, the start would want a connection string instead.
        using var host = await StartAsync(
            options => options.UseAuditSinks(new FakeSinkAdapter()),
            Audit);

        Assert.IsType<QueuedAuditSink>(host.Services.GetRequiredService<IAuditSinkPort>());
        Assert.IsType<InMemoryAuditSink>(host.Services.GetRequiredService<QueuedAuditSink>().Store);
    }

    [Fact]
    public async Task AHostTranscriptStoreWinsOverTheDefaultVendorOnTheSameKind()
    {
        using var host = await StartAsync(
            options => options.UseTranscriptStores(new FakeStoreAdapter()),
            Transcript);

        Assert.IsType<InMemoryTranscriptStore>(host.Services.GetRequiredService<ITranscriptStore>());
    }

    // ---------------------------------------------------------------------------------------------
    // providers.knowledge.analyzer. The default QdrantKnowledgeAdapter is registered inside
    // Configure, before the host's own callback runs, so a host analyzer only reaches it if the
    // wiring reapplies it afterward in FinishConfiguring. If that ordering regresses, the failure
    // below reads "no registered IKnowledgeQueryAnalyzer" instead of a network error.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHostKnowledgeQueryAnalyzerReachesTheDefaultAdapter()
    {
        // ResolveAnalyzer runs before the Qdrant client is built, so nothing on this loopback port
        // is ever contacted unless the stub analyzer already resolved. Reaching a network failure,
        // rather than "no registered IKnowledgeQueryAnalyzer", is the proof it did.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
            options =>
            {
                options.UseEmbeddings(new FakeEmbeddingAdapter());
                options.UseKnowledgeQueryAnalyzers(new StubQueryAnalyzer());
            },
            Knowledge));

        Assert.DoesNotContain("no registered", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostKnowledgePointMapperReachesTheDefaultAdapter()
    {
        // ResolveMapper runs before the Qdrant client is built, so nothing on this loopback port is
        // ever contacted unless the stub mapper already resolved. Reaching a network failure, rather
        // than "no registered IKnowledgePointMapper", is the proof it did.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
            options =>
            {
                options.UseEmbeddings(new FakeEmbeddingAdapter());
                options.UseKnowledgePointMappers(new StubPointMapper());
            },
            KnowledgeMapper));

        Assert.DoesNotContain("no registered", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // The mcp: block. McpToolSource is registered from this project, not from AgentCore.AspNetCore —
    // this test is what actually proves that wiring runs a real connection attempt, naming the
    // server that failed to connect.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A document naming an <c>mcp:</c> server whose command does not exist.</summary>
    /// <remarks>
    /// A missing executable fails <c>Process.Start</c> synchronously, so this reaches
    /// <see cref="ConfigurationLoadException"/> immediately rather than waiting out any MCP
    /// initialization timeout — the fast, offline failure this test needs.
    /// </remarks>
    private const string McpDocument = """
        apiVersion: agentcore/v1
        name: hosting-tests-mcp
        mcp:
          - id: no-such-server
            transport: stdio
            command: ["/definitely-not-a-real-binary-agentcore-test"]
            allow: ["*"]
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: fake, model: fake-model, as: reply }
        """;

    [Fact]
    public async Task AnMcpServerThatCannotBeReachedFailsTheStartNamingTheServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.AddAgentCoreHost(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(McpDocument);
            options.UseChatClients(_ => new FakeChatClientFactory());
        });

        await using var app = builder.Build();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("no-such-server", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // The CreateCase stub. It fills a gap and never takes a name the host wanted.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHostThatBindsNothingGetsTheStub()
    {
        AgentCoreOptions? options = null;
        using var host = await StartAsync(captured => options = captured);

        Assert.True(options!.Bindings.TryGetBinding(
            AgentCoreHostBuilderExtensions.CreateCaseBinding,
            out var binding));

        var result = await binding!(new JsonObject { ["summary"] = "a broken treadmill" }, TestContext.Current.CancellationToken);
        var json = Assert.IsType<JsonObject>(result);

        Assert.False((bool)json["opened"]!);
        Assert.Equal("a broken treadmill", (string?)json["summary"]);
    }

    [Fact]
    public async Task AHostThatBindsCreateCaseKeepsItsOwnDelegate()
    {
        // Registering one name twice throws, so a stub added before the callback would turn a host
        // that wants this binding into a host that cannot start at all.
        AgentCoreOptions? options = null;
        using var host = await StartAsync(configure =>
        {
            options = configure;
            configure.Bind(
                AgentCoreHostBuilderExtensions.CreateCaseBinding,
                (_, _) => ValueTask.FromResult<object?>(new JsonObject { ["opened"] = true }));
        });

        Assert.True(options!.Bindings.TryGetBinding(
            AgentCoreHostBuilderExtensions.CreateCaseBinding,
            out var binding));

        var result = await binding!([], TestContext.Current.CancellationToken);
        Assert.True((bool)Assert.IsType<JsonObject>(result)["opened"]!);
    }

    // ---------------------------------------------------------------------------------------------
    // What this extension registered, the container has to close.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOutboundHttpPipelineClosesWithTheHost()
    {
        var host = await StartAsync();
        var clients = host.Services.GetRequiredService<AgentCoreHttpClients>();

        await host.DisposeAsync();

        // The pipeline holds a container and a SocketsHttpHandler per client name. A host that stops
        // and leaves them open leaks both, and a process that restarts it leaks them again.
        Assert.Throws<ObjectDisposedException>(() => clients.CreateClient("agentcore.test"));
    }

    [Fact]
    public async Task TheLoggerFactoryClosesWithTheHost()
    {
        var host = await StartAsync();
        var loggers = host.Services.GetRequiredService<ILoggerFactory>();

        await host.DisposeAsync();

        // The container built this factory, so nothing else can be holding its providers.
        Assert.Throws<ObjectDisposedException>(() => loggers.CreateLogger("after"));
    }

    // ---------------------------------------------------------------------------------------------
    // The routes. Mapping is the other half: the registrations above answer nothing on their own.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task HealthAnswersOnTheMappedRoute()
    {
        await using var app = await StartMappedAsync();
        using HttpClient client = new() { BaseAddress = Address(app) };

        var response = await client.GetAsync(
            AgentCoreHostEndpointExtensions.HealthPattern,
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChatCompletionsAnswersOnTheDefaultRoute()
    {
        await using var app = await StartMappedAsync();
        using HttpClient client = new() { BaseAddress = Address(app) };

        // No user message is a caller mistake this endpoint names, and naming it proves the route
        // reached the endpoint rather than the 404 handler.
        var response = await PostEmptyAsync(client, ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChatCompletionsMovesWhenTheHostNamesAnotherRoute()
    {
        // A host that mounts a second OpenAI-compatible surface of its own needs this one out of the
        // way, and it must actually leave the default route behind when it moves.
        const string Moved = "/agentcore/v1/chat/completions";
        await using var app = await StartMappedAsync(Moved);
        using HttpClient client = new() { BaseAddress = Address(app) };

        Assert.Equal(
            System.Net.HttpStatusCode.BadRequest,
            (await PostEmptyAsync(client, Moved)).StatusCode);
        Assert.Equal(
            System.Net.HttpStatusCode.NotFound,
            (await PostEmptyAsync(client, ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The smallest document the schema accepts: one agent, one model, and the required pair.</summary>
    /// <remarks>
    /// A document that writes a <c>providers</c> block at all must write <c>call</c> and
    /// <c>speech</c> too, and both speech roles must name the same vendor the call does. Those kinds
    /// resolve here because the default list this library registers already names that transport —
    /// which is the point: a test writes no vendor of its own except the model.
    /// </remarks>
    private const string Document = """
        apiVersion: agentcore/v1
        name: hosting-tests
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: fake, model: fake-model, as: reply }
        """;

    /// <summary>The <c>providers</c> line that asks for the durable audit vendor.</summary>
    private const string Audit = "  audit: { kind: postgres }";

    /// <summary>The <c>providers</c> line that asks for the durable transcript vendor.</summary>
    private const string Transcript = "  transcript: { kind: postgres }";

    /// <summary>
    /// The <c>providers</c> lines for a knowledge store on an endpoint nothing listens on, so the
    /// only question a test against it can settle is whether the analyzer resolved.
    /// </summary>
    private const string Knowledge =
        "  embeddings: { kind: " + FakeEmbeddingAdapter.ProviderKind + ", model: n/a }\n"
        + "  knowledge: { kind: qdrant, endpoint: \"https://127.0.0.1:1\", collection: \"missing\", "
        + "analyzer: " + StubQueryAnalyzer.AnalyzerName + " }";

    /// <summary>Like <see cref="Knowledge"/>, but naming a mapper instead of an analyzer.</summary>
    private const string KnowledgeMapper =
        "  embeddings: { kind: " + FakeEmbeddingAdapter.ProviderKind + ", model: n/a }\n"
        + "  knowledge: { kind: qdrant, endpoint: \"https://127.0.0.1:1\", collection: \"missing\", "
        + "mapper: " + StubPointMapper.MapperName + " }";

    /// <summary>Builds a host the way a deployable does, over a fake model and no key.</summary>
    /// <param name="configure">Anything else the test says on the options.</param>
    /// <param name="providers">A further line under <c>providers</c>, or null for the document above.</param>
    /// <returns>The built host, not started.</returns>
    private static async Task<WebApplication> BuildAsync(
        Action<AgentCoreOptions>? configure = null,
        string? providers = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var document = providers is null ? Document : Document + Environment.NewLine + providers;

        builder.AddAgentCoreHost(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(document);
            options.UseChatClients(_ => new FakeChatClientFactory());
            configure?.Invoke(options);
        });

        return builder.Build();
    }

    /// <summary>Starts a host and reads its container, with nothing mapped and nothing listening.</summary>
    /// <param name="configure">Anything else the test says on the options.</param>
    /// <param name="providers">A further line under <c>providers</c>, or null for the plain document.</param>
    /// <returns>The started host.</returns>
    /// <remarks>
    /// It really starts: the document is read, and every adapter it names is opened, by the hosted
    /// lifecycle service the composition root registers. Building alone opens nothing.
    /// </remarks>
    private static async Task<WebApplication> StartAsync(
        Action<AgentCoreOptions>? configure = null,
        string? providers = null)
    {
        var app = await BuildAsync(configure, providers);

        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            // A failed start never stops what started, so disposal is the only cleanup path — and it
            // is the one a real host takes too, inside RunAsync's own finally.
            await app.DisposeAsync();
            throw;
        }

        return app;
    }

    /// <summary>A chain that holds the connection string and answers nothing for it.</summary>
    /// <remarks>
    /// A held-but-blank name fails without falling back, so these tests reach the same failure on a
    /// machine that happens to export POSTGRES_CONNECTION_STRING.
    /// </remarks>
    private sealed class EmptySecretResolver : ISecretResolverPort
    {
        public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(
                string.Equals(name, KnownSecrets.PostgresConnectionString.Name, StringComparison.Ordinal)
                    ? string.Empty
                    : null);
    }

    /// <summary>An audit vendor answering to the same kind the default list names.</summary>
    private sealed class FakeSinkAdapter : IAuditSinkAdapter
    {
        public string Kind => PostgresAuditSinkAdapter.ProviderKind;

        public ValueTask<IAuditSinkPort> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IAuditSinkPort>(new InMemoryAuditSink());
    }

    /// <summary>A transcript vendor answering to the same kind the default list names.</summary>
    private sealed class FakeStoreAdapter : ITranscriptStoreAdapter
    {
        public string Kind => PostgresTranscriptStoreAdapter.ProviderKind;

        public ValueTask<ITranscriptStore> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITranscriptStore>(new InMemoryTranscriptStore());
    }

    /// <summary>An embedding vendor whose generator is never actually asked to embed anything.</summary>
    /// <remarks>
    /// <c>QdrantKnowledgeAdapter.CreateSearchAsync</c> only needs a non-null generator to get past
    /// its own <c>providers.embeddings</c> check; the analyzer resolves right after, and every test
    /// using this adapter fails before either could reach the generator itself.
    /// </remarks>
    private sealed class FakeEmbeddingAdapter : IEmbeddingGeneratorAdapter
    {
        public const string ProviderKind = "hosting-tests-embed";

        public string Kind => ProviderKind;

        public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateGeneratorAsync(
            EmbeddingProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(new NeverUsedEmbeddingGenerator());

        private sealed class NeverUsedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
                IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Nothing in these tests should ever embed a query.");

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>An analyzer a host registers by name, distinct from either built-in.</summary>
    private sealed class StubQueryAnalyzer : IKnowledgeQueryAnalyzer
    {
        public const string AnalyzerName = "hosting-tests-stub-analyzer";

        public string Name => AnalyzerName;

        public IReadOnlyList<string> RequiredTerms(string query) => [];
    }

    /// <summary>A mapper a host registers by name, distinct from either built-in.</summary>
    private sealed class StubPointMapper : IKnowledgePointMapper
    {
        public const string MapperName = "hosting-tests-stub-mapper";

        public string Name => MapperName;

        public KnowledgeCard? Map(KnowledgePoint point) => null;
    }

    /// <summary>Builds a host, maps every route, and puts it on a real socket.</summary>
    /// <param name="chatCompletionsPattern">The route the text endpoint answers on, or null for the default.</param>
    /// <returns>The started host.</returns>
    private static async Task<WebApplication> StartMappedAsync(string? chatCompletionsPattern = null)
    {
        var app = await BuildAsync();
        app.MapAgentCoreHost(chatCompletionsPattern);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>Reads the address Kestrel took, since the tests ask for port zero.</summary>
    /// <param name="app">The started host.</param>
    /// <returns>The base address.</returns>
    private static Uri Address(WebApplication app)
        => new(app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses
            .First(), UriKind.Absolute);

    /// <summary>Posts a request with no user message, which every mapped route answers 400 to.</summary>
    /// <param name="client">The client that speaks to the host.</param>
    /// <param name="route">The route to post to.</param>
    /// <returns>The answer.</returns>
    private static Task<HttpResponseMessage> PostEmptyAsync(HttpClient client, string route)
        => client.PostAsync(
            route,
            new StringContent("{\"messages\":[]}", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

    // ---------------------------------------------------------------------------------------------
    // A transport: http MCP server is reached on this host's own pipeline, minus the one part of it
    // that does not apply to a stream.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnMcpServerIsReachedOnThePipelinesOwnHandlerChain()
    {
        CountingHandler primary = new();
        using AgentCoreHttpClients pipeline = new(primary);

        using var client = AgentCoreHostBuilderExtensions.McpHttpClient(pipeline);
        await client.GetAsync(new Uri("https://mcp.example.com/"), TestContext.Current.CancellationToken);

        // The request reached the handler the pipeline was built around, so MCP shares its proxy
        // settings, certificate configuration and logging rather than a client of its own.
        Assert.Equal(1, primary.Sends);
    }

    /// <summary>
    /// MCP over HTTP holds a stream open for the life of the session, so the pipeline's own
    /// hundred-second request deadline would sever it every hundred seconds.
    /// </summary>
    [Fact]
    public void AnMcpServersClientCarriesNoRequestDeadline()
    {
        CountingHandler primary = new();
        using AgentCoreHttpClients pipeline = new(primary);

        using var mcp = AgentCoreHostBuilderExtensions.McpHttpClient(pipeline);
        using var ordinary = pipeline.CreateClient("agentcore.tools");

        Assert.Equal(Timeout.InfiniteTimeSpan, mcp.Timeout);
        Assert.Equal(AgentCoreHttpClients.RequestDeadline, ordinary.Timeout);
    }

    /// <summary>Answers everything, and counts how often it was asked.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sends);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}

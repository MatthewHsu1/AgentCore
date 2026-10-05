using AgentCore.TestSupport;
using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Conversation.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Tools.Binding;

namespace AgentCore.Hosting.Tests
{
    /// <summary>
    /// The two calls a host makes, and the promises they carry.
    /// </summary>
    public sealed class AgentCoreHostTests
    {
        // Who wins. The host does, on every seam, because its callback runs last.

        [Fact]
        public async Task AHostChatClientFactoryWinsOverTheDefaultVendor()
        {
            // The default list names the real OpenAI adapter. If it ran after the callback it would
            // replace this factory, and the start would then want a key this test does not set.
            using WebApplication host = await StartAsync();

            Assert.NotNull(host.Services.GetService<IChatClientFactory>());
        }

        [Fact]
        public async Task AHostThatHandsOverADocumentDoesNotAlsoGetTheDefaultPath()
        {
            // Both a path and a configuration is two documents, and AddAgentCoreAsync refuses that. The
            // default path has to stand down for a host that named a document of its own.
            using WebApplication host = await StartAsync();

            Assert.NotNull(host.Services.GetService<IChatClientFactory>());
        }

        // The durable seams. Naming a vendor here is what makes providers.audit.kind: postgres startable.

        [Fact]
        public async Task ProvidersAuditPostgresReachesTheAdapterRatherThanTheSelector()
        {
            // The failure this guards is the selector's: a kind no registered adapter serves. Reaching
            // the credential instead is the proof that the default list names the PostgreSQL vendor.
            SecretResolutionException failure = await Assert.ThrowsAsync<SecretResolutionException>(
                () => StartAsync(options => options.SecretResolver = new EmptySecretResolver(), Audit));

            Assert.Contains(KnownSecrets.PostgresConnectionString.Name, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ProvidersConversationsPostgresReachesTheAdapterRatherThanTheSelector()
        {
            SecretResolutionException failure = await Assert.ThrowsAsync<SecretResolutionException>(
                () => StartAsync(options => options.SecretResolver = new EmptySecretResolver(), Conversations));

            Assert.Contains(KnownSecrets.PostgresConnectionString.Name, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ADocumentNamingNoDurableVendorStillGetsTheInProcessStores()
        {
            // Naming the PostgreSQL vendor must not make it the default. The factory answers an absent
            // block before it reads the adapter list, and this is the line that holds it to that.
            using WebApplication host = await StartAsync();

            _ = Assert.IsType<InMemoryAuditSink>(host.Services.GetRequiredService<QueuedAuditSink>().Store);
            _ = Assert.IsType<InMemoryConversationStore>(host.Services.GetRequiredService<Conversations>().Store);
            _ = Assert.IsType<InMemoryConversationStore>(host.Services.GetRequiredService<Conversations>().Store);
        }

        [Fact]
        public async Task AHostAuditSinkWinsOverTheDefaultVendorOnTheSameKind()
        {
            // UseAuditSinks is a setter, so this replaces the list rather than joining it. Were the
            // default written after the callback, the start would want a connection string instead.
            using WebApplication host = await StartAsync(
                options => options.UseAuditSinks(new FakeSinkAdapter()),
                Audit);

            _ = Assert.IsType<QueuedAuditSink>(host.Services.GetRequiredService<IAuditSinkPort>());
            _ = Assert.IsType<InMemoryAuditSink>(host.Services.GetRequiredService<QueuedAuditSink>().Store);
        }

        [Fact]
        public async Task AHostConversationStoreWinsOverTheDefaultVendorOnTheSameKind()
        {
            using WebApplication host = await StartAsync(
                options => options.UseConversationStores(new FakeConversationStoreAdapter()),
                Conversations);

            _ = Assert.IsType<InMemoryConversationStore>(host.Services.GetRequiredService<Conversations>().Store);
        }

        // providers.knowledge.analyzer. The default QdrantKnowledgeAdapter is registered inside
        // Configure, before the host's own callback runs, so a host analyzer only reaches it if the
        // wiring reapplies it afterward in FinishConfiguring. If that ordering regresses, the failure
        // below reads "no registered IKnowledgeQueryAnalyzer" instead of a network error.

        [Fact]
        public async Task AHostKnowledgeQueryAnalyzerReachesTheDefaultAdapter()
        {
            // ResolveAnalyzer runs before the Qdrant client is built, so nothing on this loopback port
            // is ever contacted unless the stub analyzer already resolved. Reaching a network failure,
            // rather than "no registered IKnowledgeQueryAnalyzer", is the proof it did.
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
                options =>
                {
                    _ = options.UseEmbeddings(new FakeEmbeddingAdapter());
                    _ = options.UseKnowledgeQueryAnalyzers(new StubQueryAnalyzer());
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
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
                options =>
                {
                    _ = options.UseEmbeddings(new FakeEmbeddingAdapter());
                    _ = options.UseKnowledgePointMappers(new StubPointMapper());
                },
                KnowledgeMapper));

            Assert.DoesNotContain("no registered", failure.Message, StringComparison.Ordinal);
        }

        // The mcp: block. McpToolSource is registered from this project, not from AgentCore.AspNetCore —
        // this test is what actually proves that wiring runs a real connection attempt, naming the
        // server that failed to connect.

        /// <summary>A document naming an <c>mcp:</c> server whose command does not exist.</summary>
        private const string McpDocument = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        mcp:
          - id: no-such-server
            transport: stdio
            command: ["/definitely-not-a-real-binary-agentcore-test"]
            allow: ["*"]
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: fake, model: fake-model, as: reply }
        """;

        [Fact]
        public async Task AnMcpServerThatCannotBeReachedFailsTheStartNamingTheServer()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
            _ = builder.Logging.ClearProviders();

            _ = builder.AddAgentCoreHost(options =>
            {
                options.Configuration = ConfigurationLoader.LoadYaml(McpDocument);
                _ = options.UseChatClients(_ => new RecordingChatClientFactory(new FakeChatClient()));
            });

            await using WebApplication app = builder.Build();

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => app.StartAsync(TestContext.Current.CancellationToken));

            Assert.Contains("no-such-server", failure.Message, StringComparison.Ordinal);
        }

        // The CreateCase stub. It fills a gap and never takes a name the host wanted.

        [Fact]
        public async Task AHostThatBindsNothingGetsTheStub()
        {
            AgentCoreOptions? options = null;
            using WebApplication host = await StartAsync(captured => options = captured);

            Assert.True(options!.Bindings.TryGetBinding(
                AgentCoreHostBuilderExtensions.CreateCaseBinding,
                out ToolBinding? binding));

            object? result = await binding!(new JsonObject { ["summary"] = "a broken treadmill" }, TestContext.Current.CancellationToken);
            JsonObject json = Assert.IsType<JsonObject>(result);

            Assert.False((bool)json["opened"]!);
            Assert.Equal("a broken treadmill", (string?)json["summary"]);
        }

        [Fact]
        public async Task AHostThatBindsCreateCaseKeepsItsOwnDelegate()
        {
            // Registering one name twice throws, so a stub added before the callback would turn a host
            // that wants this binding into a host that cannot start at all.
            AgentCoreOptions? options = null;
            using WebApplication host = await StartAsync(configure =>
            {
                options = configure;
                _ = configure.Bind(
                    AgentCoreHostBuilderExtensions.CreateCaseBinding,
                    (_, _) => ValueTask.FromResult<object?>(new JsonObject { ["opened"] = true }));
            });

            Assert.True(options!.Bindings.TryGetBinding(
                AgentCoreHostBuilderExtensions.CreateCaseBinding,
                out ToolBinding? binding));

            object? result = await binding!([], TestContext.Current.CancellationToken);
            Assert.True((bool)Assert.IsType<JsonObject>(result)["opened"]!);
        }

        // What this extension registered, the container has to close.

        [Fact]
        public async Task TheOutboundHttpPipelineClosesWithTheHost()
        {
            WebApplication host = await StartAsync();
            AgentCoreHttpClients clients = host.Services.GetRequiredService<AgentCoreHttpClients>();

            await host.DisposeAsync();

            // The pipeline holds a container and a SocketsHttpHandler per client name. A host that stops
            // and leaves them open leaks both, and a process that restarts it leaks them again.
            _ = Assert.Throws<ObjectDisposedException>(() => clients.CreateClient("agentcore.test"));
        }

        [Fact]
        public async Task TheLoggerFactoryClosesWithTheHost()
        {
            WebApplication host = await StartAsync();
            ILoggerFactory loggers = host.Services.GetRequiredService<ILoggerFactory>();

            await host.DisposeAsync();

            // The container built this factory, so nothing else can be holding its providers.
            _ = Assert.Throws<ObjectDisposedException>(() => loggers.CreateLogger("after"));
        }
        // The host's own callback runs after the turnkey wiring, so the resolver it sets is the one every
        // vendor must read, openai-live included.
        [Fact]
        public async Task TheOpenAiLiveKeysResolveThroughTheResolverTheHostSet()
        {
            MapSecretResolver secrets = new MapSecretResolver()
                .With(KnownSecrets.OpenAiApiKeyName, "sk-test")
                .With(KnownSecrets.OpenAiWebhookSecretName, "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");

            await using WebApplication host = await StartAsync(options =>
            {
                options.Configuration = ConfigurationLoader.LoadYaml(LiveDocument);
                options.SecretResolver = secrets;
            });

            Assert.Contains(KnownSecrets.OpenAiWebhookSecretName, secrets.Asked);
        }

        /// <summary>The <c>providers</c> line that asks for the durable audit vendor.</summary>
        private const string LiveDocument = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        providers:
          conversation:
            kind: openai-live
            live:
              instructions: "Be brief."
          llm:
            - { kind: fake, model: fake-model, as: reply }
        """;

        private const string Audit = "  audit: { kind: postgres }";

        /// <summary>The <c>providers</c> line that asks for the durable transcript vendor.</summary>

        /// <summary>The <c>providers</c> line that asks for the durable conversation store vendor.</summary>
        private const string Conversations = "  conversations: { kind: postgres }";

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

        /// <summary>Starts a host and reads its container, with nothing mapped and nothing listening.</summary>
        /// <param name="configure">Anything else the test says on the options.</param>
        /// <param name="providers">A further line under <c>providers</c>, or null for the plain document.</param>
        /// <returns>The started host.</returns>
        private static async Task<WebApplication> StartAsync(
            Action<AgentCoreOptions>? configure = null,
            string? providers = null)
        {
            WebApplication app = await HostingTestHost.BuildAsync(configure, providers);

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

        /// <summary>Answers an empty connection string and the relay's key, and nothing else.</summary>
        private sealed class EmptySecretResolver : ISecretResolverPort
        {
            public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult(name switch
                {
                    KnownSecrets.PostgresConnectionStringName => string.Empty,
                    KnownSecrets.TelnyxRelayKeyName => HostingTestHost.RelayKey,
                    _ => null,
                });
            }
        }

        /// <summary>An audit vendor answering to the same kind the default list names.</summary>
        private sealed class FakeSinkAdapter : IAuditSinkAdapter
        {
            public string Kind => PostgresAuditSinkAdapter.ProviderKind;

            public ValueTask<IAuditSinkPort> OpenAsync(
                VendorProviderConfiguration entry,
                ISecretResolverPort? secrets,
                CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IAuditSinkPort>(new InMemoryAuditSink());
            }
        }


        /// <summary>A conversation store vendor answering to the same kind the default list names.</summary>
        private sealed class FakeConversationStoreAdapter : IConversationStoreAdapter
        {
            public string Kind => PostgresConversationStoreAdapter.ProviderKind;

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry,
                ISecretResolverPort? secrets,
                CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IConversationStore>(new InMemoryConversationStore());
            }
        }

        /// <summary>An embedding vendor whose generator is never actually asked to embed anything.</summary>
        private sealed class FakeEmbeddingAdapter : IEmbeddingGeneratorAdapter
        {
            public const string ProviderKind = "hosting-tests-embed";

            public string Kind => ProviderKind;

            public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateGeneratorAsync(
                EmbeddingProviderConfiguration entry,
                ISecretResolverPort? secrets,
                CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(new NeverUsedEmbeddingGenerator());
            }

            private sealed class NeverUsedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
            {
                public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
                    IEnumerable<string> values,
                    EmbeddingGenerationOptions? options = null,
                    CancellationToken cancellationToken = default)
                {
                    throw new InvalidOperationException("Nothing in these tests should ever embed a query.");
                }

                public object? GetService(Type serviceType, object? serviceKey = null)
                {
                    return null;
                }

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

            public IReadOnlyList<string> RequiredTerms(string query)
            {
                return [];
            }
        }

        /// <summary>A mapper a host registers by name, distinct from either built-in.</summary>
        private sealed class StubPointMapper : IKnowledgePointMapper
        {
            public const string MapperName = "hosting-tests-stub-mapper";

            public string Name => MapperName;

            public KnowledgeCard? Map(KnowledgePoint point)
            {
                return null;
            }
        }

        // A transport: http MCP server is reached on this host's own pipeline, minus the one part of it
        // that does not apply to a stream.

        [Fact]
        public async Task AnMcpServerIsReachedOnThePipelinesOwnHandlerChain()
        {
            CountingHandler primary = new();
            using AgentCoreHttpClients pipeline = new(primary);

            using HttpClient client = AgentCoreHostBuilderExtensions.McpHttpClient(pipeline);
            _ = await client.GetAsync(new Uri("https://mcp.example.com/"), TestContext.Current.CancellationToken);

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

            using HttpClient mcp = AgentCoreHostBuilderExtensions.McpHttpClient(pipeline);
            using HttpClient ordinary = pipeline.CreateClient("agentcore.tools");

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
                _ = Interlocked.Increment(ref _sends);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }
        }
    }
}

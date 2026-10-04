using System.Runtime.CompilerServices;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using AgentCore.TestSupport;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Tools;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Xunit;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using Microsoft.Agents.AI;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;
using AgentCore.Domain;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.DependencyInjection
{
    /// <summary>
    /// The composition root. It loads, validates, resolves, compiles, and registers, in that order.
    /// </summary>
    public sealed class AddAgentCoreTests
    {
        // The same agent, reachable on two entries.
        private const string TwoEntryYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
        entries:
          main:
            agent: only
          other:
            agent: only
        """;

        // The same agent, and a document that names a telemetry vendor.
        private const string TelemetryYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
          telemetry: { kind: test }
        entries:
          main:
            agent: only
        """;

        // The same agent, and a document that names a moderation vendor.
        private const string ModeratedYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
          moderation: { kind: test }
        entries:
          main:
            agent: only
        """;

        // The same agent, served by a vendor this host's fake adapter does not answer to.
        private const string OtherVendorYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{SpeechAndConversation}}
          llm:
            - { kind: anthropic, model: claude-sonnet-5, as: reply }
        entries:
          main:
            agent: only
        """;

        // The same agent, with both tunable keys of the document set away from their default.
        private const string TunedYaml =
            $$"""
        apiVersion: agentcore/v1
        fallbackReply: "One moment please. I will try that again."
        evaluation:
          sampleRate: 1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
        entries:
          main:
            agent: only
        """;

        private const string BindingYaml =
            $$"""
        apiVersion: agentcore/v1
        tools:
          - id: create_case
            kind: binding
            binds: CreateCase
            description: Open a service case for a human agent.
            parameters:
              type: object
              properties: { summary: { type: string } }
              required: [ summary ]
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ create_case ] }
        {{MinimalProviders}}
        entries:
          main:
            agent: only
        """;

        private const string SecretYaml =
            $$"""
        apiVersion: agentcore/v1
        tools:
          - id: lookup_order
            kind: http
            description: Read one order by its identifier.
            parameters:
              type: object
              properties: { orderId: { type: string } }
              required: [ orderId ]
            request:
              method: GET
              url: "https://api.example.com/orders/{orderId}"
              headers: { Authorization: "Bearer ${secret:orders-api-key}" }
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        {{MinimalProviders}}
        entries:
          main:
            agent: only
        """;

        // A guarded edge on each exit of the start node.
        // Load-time validation proves the two guards exclusive, so exactly one edge fires for each conversation.
        private const string GuardedGraphYaml =
            $$"""
          apiVersion: agentcore/v1
          state:
            escalate: { type: boolean, writer: extractor, default: false }
          guards:
            wants_human: { "===": [ { var: escalate }, true ] }
            stays_with_bot: { "===": [ { var: escalate }, false ] }
          agents:
            items:
              - { id: router, model: { ref: router } }
              - { id: human, model: { ref: human } }
              - { id: bot, model: { ref: bot } }
          entries:
            main:
              graph:
                nodes:
                  - { id: route, agent: router, start: true }
                  - { id: escalated, agent: human, output: true }
                  - { id: handled, agent: bot, output: true }
                edges:
                  - { from: route, to: escalated, when: wants_human }
                  - { from: route, to: handled, when: stays_with_bot }
          providers:
            conversation:   { kind: telnyx-relay }
            speech:
              stt: { kind: telnyx-relay }
              tts: { kind: telnyx-relay }
            llm:
              - { kind: openai, model: gpt-4.1-mini, as: router }
              - { kind: openai, model: gpt-4.1-mini, as: human }
              - { kind: openai, model: gpt-4.1-mini, as: bot }
          """;

        // A stage names a target that policy.stages does not declare, so validation fails the load.
        private const string BrokenYaml =
            $$"""
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: only, instructions: "I answer everything" }
          {{MinimalProviders}}
          entries:
            main:
              policy:
                initial: start
                stages:
                  - { id: start, agent: only, to: [ { stage: nowhere } ] }
          """;

        // A state slot's from: names a tool no tools: entry declares, and no mcp: server offers it
        // either, so nothing in the served set ever resolves it.
        private const string UndeclaredToolYaml =
            $$"""
        apiVersion: agentcore/v1
        state:
          orderStatus: { type: string, writer: tool, from: lookup_order.status }
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
        entries:
          main:
            agent: only
        """;

        // An agent's tools: names an id nothing serves. Unlike UndeclaredToolYaml, the fault sits in
        // agents.items[].tools rather than state:, so the pointer must name the agent and not a bare
        // /tools.
        private const string UndeclaredAgentToolYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ no_such_tool ] }
        {{MinimalProviders}}
        entries:
          main:
            agent: only
        """;

        // Declares no tools: at all. 'discovered_only' is served only by a fake IToolSource the test
        // registers, never named anywhere in the document itself, so the only way this boots is if the
        // reference pass resolves against what got discovered rather than what got declared.
        private const string DiscoveredOnlyToolYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ discovered_only ] }
        {{MinimalProviders}}
        entries:
          main:
            agent: only
        """;

        // A kind: agent tool reaches no source at all: the compiler builds it once the agent it names
        // has compiled, so the registry never holds it. The reference pass must still let front's
        // tools: [ ask_specialist ] through, or every delegating document fails to boot.
        // A declared kind: agent tool whose id a registered source also discovers. The collision is only
        // found after every source has answered, so by then the source is open.
        private const string CollidingAgentToolYaml =
            $$"""
          apiVersion: agentcore/v1
          tools:
            - id: shared_id
              kind: agent
              agent: specialist
              description: Ask the specialist one product question.
              parameters:
                type: object
                properties: { question: { type: string } }
                required: [ question ]
          agents:
            items:
              - { id: front, instructions: "the caller talks to me", tools: [ shared_id ] }
              - { id: specialist, instructions: "I answer product questions" }
          {{MinimalProviders}}
          entries:
            main:
              policy:
                initial: talk
                stages:
                  - { id: talk, agent: front, terminal: true }
          """;

        private const string DelegatingAgentToolYaml =
            $$"""
          apiVersion: agentcore/v1
          tools:
            - id: ask_specialist
              kind: agent
              agent: specialist
              description: Ask the specialist one product question.
              parameters:
                type: object
                properties: { question: { type: string } }
                required: [ question ]
          agents:
            items:
              - { id: front, instructions: "the caller talks to me", tools: [ ask_specialist ] }
              - { id: specialist, instructions: "I answer product questions" }
          {{MinimalProviders}}
          entries:
            main:
              policy:
                initial: talk
                stages:
                  - { id: talk, agent: front, terminal: true }
          """;

        // BrokenYaml's structural defect (an unreachable policy transition), plus an mcp: server whose
        // command names a binary that does not exist. The structural
        // error below must surface without AgentCore ever trying to reach that server: a missing
        // executable fails Process.Start synchronously, so if discovery ran first this would instead
        // report the MCP failure. See AddAgentCore_TheStructuralFaultSurfaces_BeforeMcpIsEverAsked.
        private const string StructuralFaultPlusUnreachableMcpYaml =
            $$"""
          apiVersion: agentcore/v1
          mcp:
            - id: bogus-server
              transport: stdio
              command: ["/definitely-not-a-real-binary-agentcore-task5-test"]
              allow: ["*"]
          agents:
            items:
              - { id: only, instructions: "I answer everything" }
          {{MinimalProviders}}
          entries:
            main:
              policy:
                initial: start
                stages:
                  - { id: start, agent: only, to: [ { stage: nowhere } ] }
          """;

        [Fact]
        public async Task AddAgentCore_RegistersTheCompiledAgentAsAProcessSingleton()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            CompiledAgent first = provider.GetRequiredService<IReadOnlyDictionary<string, CompiledAgent>>()["main"];
            CompiledAgent second = provider.GetRequiredService<IReadOnlyDictionary<string, CompiledAgent>>()["main"];

            Assert.Same(first, second);
            Assert.Equal("main", first.Name);

            // The registry compiled once, and every conversation shares that one result.
            Assert.Equal(1, provider.GetRequiredService<CompiledAgentRegistry>().CompileCount);
        }

        [Fact]
        public async Task AddAgentCore_RegistersOneSessionFactoryThatBuildsANewSessionForEachConversation()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);
            IConversationSessionFactory factory = provider.GetRequiredService<EntryRegistry>().ForFactory("main");

            Assert.Same(factory, provider.GetRequiredService<EntryRegistry>().ForFactory("main"));

            // A ConversationSession belongs to one conversation, so it is not a singleton and the container holds none.
            ConversationSession first = factory.Create();
            ConversationSession second = factory.Create();
            Assert.NotSame(first, second);
            Assert.NotEqual(first.ConversationId, second.ConversationId);
        }

        [Fact]
        public async Task AddAgentCore_RegistersTheKnowledgePortTheHostBound()
        {
            FacetCapablePort port = new();

            using StartedHost provider = await BuildAsync(
                OneAgentYaml, options => options.UseKnowledgeRetrieval(_ => port));

            Assert.Same(port, provider.GetRequiredService<IKnowledgeRetrievalPort>());
        }

        [Fact]
        public async Task AddAgentCore_TheResolvedPortAnswersWhatElseItServes()
        {
            // What a consumer actually does with it: resolve the one port, then ask that port for the
            // capability it needs. Registering each capability separately would hand out a second
            // object for the same store, and a store that serves none would have to be registered as
            // null anyway.
            FacetCapablePort port = new();

            using StartedHost provider = await BuildAsync(
                OneAgentYaml, options => options.UseKnowledgeRetrieval(_ => port));

            IKnowledgeRetrievalPort knowledge = provider.GetRequiredService<IKnowledgeRetrievalPort>();

            Assert.Same(port, knowledge.GetService<IKnowledgeFacetReadPort>());
        }

        [Fact]
        public async Task AddAgentCore_ADocumentThatReadsNoKnowledge_ResolvesNoPort()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            Assert.Null(provider.GetService<IKnowledgeRetrievalPort>());
        }

        [Fact]
        public async Task AddAgentCore_RegistersTheAgentShimAsAProcessSingleton()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            AgentCoreAgent agent = provider.GetRequiredService<EntryRegistry>().ForAgent("main");

            Assert.Same(agent, provider.GetRequiredService<EntryRegistry>().ForAgent("main"));
            Assert.Equal("main", agent.Name);

            // One session of the shim is one conversation, drawn from the same factory the rest of the host
            // uses, so the two seams describe the same conversations.
            AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(session.GetService<ConversationSession>());
        }

        [Fact]
        public async Task AddAgentCore_RegistersTheInMemorySessionsByDefault()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            IConversationSessions sessions = provider.GetRequiredService<EntryRegistry>().Sessions;

            _ = Assert.IsType<InMemoryConversationSessions>(sessions);
            Assert.Same(sessions, provider.GetRequiredService<EntryRegistry>().Sessions);
        }

        /// <summary>
        /// The one thing deferring the boot to host start has to guarantee: a service the document
        /// produced cannot be read before the document has been read. A provider nobody started answers
        /// with a refusal that names the fix, and never with a half-built graph or a null.
        /// </summary>
        [Fact]
        public void AServiceReadFromAProviderNobodyStarted_RefusesAndSaysWhatToDo()
        {
            ServiceCollection services = new();
            ConfigureServices(services, OneAgentYaml, null);

            using ServiceProvider provider = services.BuildServiceProvider();

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                provider.GetRequiredService<IReadOnlyDictionary<string, CompiledAgent>>);

            Assert.Contains("has not booted", failure.Message, StringComparison.Ordinal);
            Assert.Contains("StartAsync", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AddAgentCore_UsesTheSessionsTheHostBound()
        {
            CountingConversationSessions mine = new();

            using StartedHost provider = await BuildAsync(
                OneAgentYaml, options => options.UseConversationSessions(_ => mine));

            // A distributed owner replaces the default one, and the default steps aside.
            Assert.Same(mine, provider.GetRequiredService<EntryRegistry>().Sessions);
        }

        [Fact]
        public async Task AddAgentCore_BuildsOneOwnerOverEveryEntrysFactory()
        {
            // The owner is opened once for the whole app, not once per entry: the opener sees every entry's
            // factory in one call, and every entry reads and writes through the one owner it returns.
            List<IReadOnlyDictionary<string, IConversationSessionFactory>> opened = [];

            using StartedHost provider = await BuildAsync(TwoEntryYaml, options => options.UseConversationSessions(
                factories =>
                {
                    opened.Add(factories);
                    return new InMemoryConversationSessions(
                        factories, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System);
                }));

            IConversationSessionRegistry registry = provider.GetRequiredService<IConversationSessionRegistry>();

            IReadOnlyDictionary<string, IConversationSessionFactory> factories = Assert.Single(opened);
            Assert.Equal(["main", "other"], factories.Keys.Order(StringComparer.Ordinal));

            // One owner, not one per entry: a session "main" opened is seen as held — and "other" refused —
            // through the very same registry.Sessions. Two separate owners would let "other" open it too.
            _ = await registry.Sessions.GetOrOpenAsync("main", "conversation-one-owner", null, TestContext.Current.CancellationToken);
            _ = await Assert.ThrowsAsync<ConversationInUseException>(
                () => registry.Sessions.GetOrOpenAsync("other", "conversation-one-owner", null, TestContext.Current.CancellationToken).AsTask());
        }

        [Fact]
        public async Task AddAgentCore_RegistersTheSessionRegistryAsAPublicPort()
        {
            // A consumer reaches the one owner through the port, never through the internal registry, and
            // never through a bare IConversationSessions registration of its own.
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            IConversationSessionRegistry registry = provider.GetRequiredService<IConversationSessionRegistry>();

            Assert.Equal(["main"], registry.Entries);
            Assert.Same(provider.GetRequiredService<EntryRegistry>().Sessions, registry.Sessions);
            Assert.Null(provider.GetService<IConversationSessions>());
        }

        // Telemetry shuts down with the host. The container owns the session, so its disposal is the
        // flush — which is the one path a start that failed also reaches.
        [Fact]
        public async Task AddAgentCore_RegistersTheTelemetrySessionTheDocumentNames()
        {
            (IHost? host, FlushRecordingTelemetryAdapter? adapter) = await BuildTelemetryHostAsync();

            using (host)
            {
                await host.StartAsync(TestContext.Current.CancellationToken);

                // A host that reads its own spans and metrics resolves this. Nothing in this library
                // does, so only a test holds it to being there at all.
                Assert.Same(adapter.Session, host.Services.GetRequiredService<ITelemetrySession>());
            }
        }

        [Fact]
        public async Task AddAgentCore_RegistersNoTelemetrySessionWhenTheHostBindsNoVendor()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            Assert.Null(provider.GetService<ITelemetrySession>());
        }

        [Fact]
        public async Task AddAgentCore_FlushesTheTelemetrySessionWhenTheHostShutsDown()
        {
            (IHost? host, FlushRecordingTelemetryAdapter? adapter) = await BuildTelemetryHostAsync();

            await host.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, adapter.Session.Flushes);

            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();

            Assert.Equal(1, adapter.Session.Flushes);
        }

        [Fact]
        public async Task AddAgentCore_FlushesTheTelemetrySessionOnceWhenTheHostIsDisposedTwice()
        {
            (IHost? host, FlushRecordingTelemetryAdapter? adapter) = await BuildTelemetryHostAsync();

            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);

            // An adapter's session is not required to survive being drained twice, so the second call
            // has to be a no-op.
            host.Dispose();
            host.Dispose();

            Assert.Equal(1, adapter.Session.Flushes);
        }

        [Fact]
        public async Task AddAgentCore_ClosesTheConversationStoreWhenTheHostShutsDown()
        {
            RecordingConversationStore store = new();
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            ConfigureServices(
                builder.Services,
                VendorTranscriptYaml,
                options => options.UseConversationStores(new TestConversationStoreAdapter(store)));

            IHost host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();

            Assert.True(store.Closed);
        }

        [Fact]
        public async Task AddAgentCore_ClosesTheKnowledgePortWhenTheHostShutsDown()
        {
            // KnowledgeStartup.OpenAsync's result used to be discarded with `_ = await ...`, so a
            // successful open -- a QdrantClient in production -- was never tracked against the boot and
            // outlived host shutdown. This proves the port the adapter built is closed the same way the
            // conversation store above is.
            DisposeTrackingKnowledgeAdapter adapter = new();
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            ConfigureServices(
                builder.Services,
                VendorKnowledgeYaml,
                options => options.UseKnowledgeStores(adapter));

            IHost host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(adapter.Built);
            Assert.False(adapter.Built.Closed);

            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();

            Assert.True(adapter.Built.Closed);
        }

        [Fact]
        public async Task AHostRegisteredDisposableToolSource_IsClosedWhenTheHostShutsDown()
        {
            // Disposal happens once, when the container closes the boot that owns the source — the same
            // route McpToolSource is closed through, proved here with no MCP server involved. The
            // reference kept below is exactly the case that makes the risk small rather than zero:
            // AddToolSource's factory could be called more than once by a host that keeps its own
            // reference to what it returns, and closing it anyway costs little because the host was
            // about to lose it either way.
            DisposeTrackingToolSource source = new();
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            ConfigureServices(
                builder.Services,
                OneAgentYaml,
                options => options.AddToolSource(_ => source));

            IHost host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);

            Assert.False(source.Disposed);

            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();

            Assert.True(source.Disposed);
        }

        // The document picks the vendor, and no code names one: the point of the adapter seam.
        [Fact]
        public async Task TheAdapterOverload_LetsTheDocumentPickTheVendorByItsKind()
        {
            // 'kind: openai' selects the adapter registered under that kind. The host lists what it
            // supports, once, and the document decides which entry runs.
            MapModelCatalogPort catalog = new MapModelCatalogPort().With("openai", "gpt-4.1-mini", 128_000);

            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options
                .UseModelCatalog(catalog)
                .UseChatClients(
                    new FakeChatClientAdapter("openai", () => new FragmentingChatClient("routed")),
                    new FakeChatClientAdapter("anthropic", () => new FragmentingChatClient("wrong vendor"))));

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create();
            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Equal("routed", turn.ReplyText);
        }

        [Fact]
        public async Task AKindNoRegisteredAdapterServes_FailsTheStartAndNamesBothSides()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
                OtherVendorYaml,
                options => options.UseChatClients(
                    new FakeChatClientAdapter("openai", () => new FragmentingChatClient("hello")))));

            // The message names the kind the document wrote and the kinds the host registers, so the
            // reader knows which side to change.
            Assert.Contains("anthropic", failure.Message, StringComparison.Ordinal);
            Assert.Contains("'openai'", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AnAsyncSeam_BuildsItsFactoryWithNoBlockedThread()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.UseChatClients(
                async (startup, cancellationToken) =>
                {
                    await Task.Yield();
                    return new RoutingChatClientFactory(new FragmentingChatClient("awaited"));
                }));

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create();
            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Equal("awaited", turn.ReplyText);
        }

        [Fact]
        public async Task AToolSource_SeesTheChatClientFactoryAlreadyBuilt()
        {
            // The seam that builds the factory only runs once, so a null capture here means the tools
            // were built before it ran — exactly the ordering builtin tools depend on.
            IChatClientFactory? builtFactory = null;
            IChatClientFactory? seenWhenToolsWereBuilt = null;

            using StartedHost provider = await BuildAsync(OneAgentYaml, options =>
            {
                _ = options.UseChatClients((_, _) =>
                {
                    builtFactory = new RoutingChatClientFactory(new FragmentingChatClient("hello"));
                    return ValueTask.FromResult(builtFactory);
                });

                _ = options.AddToolSource(_ => new SpyToolSource(() => seenWhenToolsWereBuilt = builtFactory));
            });

            Assert.NotNull(provider.GetRequiredService<IReadOnlyDictionary<string, CompiledAgent>>()["main"]);
            Assert.NotNull(seenWhenToolsWereBuilt);
            Assert.Same(builtFactory, seenWhenToolsWereBuilt);
        }

        [Fact]
        public async Task ABindingTool_ReachesTheDelegateTheHostRegistered()
        {
            using StartedHost provider = await BuildAsync(
                BindingYaml,
                options => options.Bind("CreateCase", (_, _) => ValueTask.FromResult<object?>(new JsonObject())));

            ToolBindingRegistry bindings = provider.GetRequiredService<ToolBindingRegistry>();

            Assert.True(bindings.Contains("CreateCase"));
            Assert.Equal(1, bindings.Count);
        }

        [Fact]
        public async Task ABindingToolWithNoDelegate_FailsTheStartAndNamesTheBinding()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(BindingYaml));

            Assert.Contains("CreateCase", failure.Message, StringComparison.Ordinal);
            Assert.Contains("did not register", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ASecretReference_ResolvesOnceAtStartup()
        {
            using HttpClient client = new();
            MapSecretResolver resolver = new();
            _ = resolver.With("orders-api-key", "a-value-no-message-repeats");

            using StartedHost provider = await BuildAsync(
                SecretYaml,
                options =>
                {
                    options.SecretResolver = resolver;
                    _ = options.AddToolSource(startup => new HttpToolSource(client, startup.Secrets));
                });

            ResolvedSecrets secrets = provider.GetRequiredService<ResolvedSecrets>();

            Assert.True(secrets.Contains("orders-api-key"));

            // A resolved set lands in a log line sooner or later, so it reports the count and never a value.
            Assert.DoesNotContain("a-value-no-message-repeats", secrets.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task ASecretReferenceWithNoResolver_FailsTheStartAndNamesTheSecret()
        {
            using HttpClient client = new();

            SecretResolutionException failure = await Assert.ThrowsAsync<SecretResolutionException>(() => BuildAsync(
                SecretYaml,
                options => options.AddToolSource(startup => new HttpToolSource(client, startup.Secrets))));

            Assert.Equal("orders-api-key", failure.SecretName);
        }

        [Fact]
        public async Task AGuardedGraph_Starts()
        {
            using StartedHost provider = await BuildGuardedGraphAsync();

            CompiledAgent compiled = provider.GetRequiredService<IReadOnlyDictionary<string, CompiledAgent>>()["main"];

            // The document passes all eight checks and compiles too. AddAgentCore binds the guard
            // evaluator, so a guarded edge is reachable from here.
            Assert.Equal(CompiledAgentShape.ExplicitGraph, compiled.Shape);
            Assert.Equal("main", compiled.Name);
        }

        [Theory]
        [InlineData(true, "ESCALATED", "HANDLED")]
        [InlineData(false, "HANDLED", "ESCALATED")]
        public async Task AGuardedGraph_TakesTheEdgeTheStateOfTheConversationNames(bool escalate, string taken, string refused)
        {
            using StartedHost provider = await BuildGuardedGraphAsync();
            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create();
            _ = session.State.TryWrite("escalate", escalate);

            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Contains(taken, turn.ReplyText, StringComparison.Ordinal);
            Assert.DoesNotContain(refused, turn.ReplyText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AGuardedGraph_KeepsTwoConversationsApartWhenTheyRunAtTheSameTime()
        {
            using StartedHost provider = await BuildGuardedGraphAsync();
            IConversationSessionFactory sessions = provider.GetRequiredService<EntryRegistry>().ForFactory("main");
            CancellationToken token = TestContext.Current.CancellationToken;

            ConversationSession escalated = sessions.Create();
            _ = escalated.State.TryWrite("escalate", true);
            ConversationSession handled = sessions.Create();
            _ = handled.State.TryWrite("escalate", false);

            // One compiled graph, two conversations, two edges. Neither conversation reads the state of the other.
            TurnResult[] turns = await Task.WhenAll(
                escalated.RunTurnAsync("hello", token),
                handled.RunTurnAsync("hello", token));

            Assert.Contains("ESCALATED", turns[0].ReplyText, StringComparison.Ordinal);
            Assert.Contains("HANDLED", turns[1].ReplyText, StringComparison.Ordinal);
            Assert.Equal(1, provider.GetRequiredService<CompiledAgentRegistry>().CompileCount);
        }

        // The online evaluation path is closed by default, so the sample rate is 0.
        [Fact]
        public async Task AddAgentCore_RegistersTheEvaluationSeamWithTheOnlinePathClosed()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            EvaluatorRegistry registry = provider.GetRequiredService<EvaluatorRegistry>();
            EvaluationSampler sampler = provider.GetRequiredService<EvaluationSampler>();

            // fault_code calls no model, so it is the one evaluator that is safe by default.
            Assert.True(registry.Contains("fault_code"));

            // A judge must never block a turn, and the offline gate has not proved the evaluators
            // yet. A rate of 0 draws no number and conversations nothing.
            Assert.Equal(0, sampler.Rate);
            Assert.False(sampler.ShouldSample());

            _ = Assert.IsType<InMemoryEvaluationScorePublisher>(provider.GetRequiredService<IEvaluationScorePublisher>());
        }

        [Fact]
        public async Task AddAgentCore_RegistersNoModeratorWhenTheHostBindsNoVendor()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            EvaluatorRegistry registry = provider.GetRequiredService<EvaluatorRegistry>();

            // A host that registers no moderation vendor moderates nothing, and every turn reaches the
            // model. Moderation needs a vendor account, and a library that refused to start without one
            // could not be used in a test.
            Assert.False(registry.Contains(PromptModerator.ModerationEvaluatorName));
            Assert.Null(PromptModerator.FromRegistry(registry));
        }

        [Fact]
        public async Task AddAgentCore_BuildsNoModeratorWhenTheDocumentNamesNoProvider()
        {
            FakeModerationAdapter adapter = new("test", new AlwaysFlagsEvaluator());

            // The vendor is registered and the document names none, so the adapter is never asked to
            // build anything. Registering a vendor costs nothing until a document names it.
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.UseModeration(adapter));

            Assert.False(provider.GetRequiredService<EvaluatorRegistry>()
                .Contains(PromptModerator.ModerationEvaluatorName));
            Assert.Equal(0, adapter.Builds);
        }

        [Fact]
        public async Task AddAgentCore_BuildsTheModerationVendorTheDocumentNames()
        {
            FakeModerationAdapter adapter = new("test", new AlwaysFlagsEvaluator());

            using StartedHost provider = await BuildAsync(ModeratedYaml, options => options.UseModeration(adapter));

            EvaluatorRegistry registry = provider.GetRequiredService<EvaluatorRegistry>();

            // The same object serves the turn loop and the offline golden set: an evaluator is written once and used twice.
            Assert.Equal(1, adapter.Builds);
            Assert.True(registry.Contains(PromptModerator.ModerationEvaluatorName));
            Assert.True(registry.Contains("fault_code"));
        }

        [Fact]
        public async Task AddAgentCore_MatchesTheModerationKindWithoutRegardToCase()
        {
            // A vendor name is written by a human, exactly as the knowledge kinds are matched.
            FakeModerationAdapter adapter = new("TEST", new AlwaysFlagsEvaluator());

            using StartedHost provider = await BuildAsync(ModeratedYaml, options => options.UseModeration(adapter));

            Assert.Equal(1, adapter.Builds);
        }

        [Fact]
        public async Task AddAgentCore_FailsWhenTheDocumentNamesAModerationKindThisHostDoesNotRegister()
        {
            ConfigurationLoadException error = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await BuildAsync(
                    ModeratedYaml,
                    options => options.UseModeration(new FakeModerationAdapter("other", new AlwaysFlagsEvaluator()))));

            // The message names what this host does register, so the fix is obvious from the failure.
            Assert.Contains("test", error.Message, StringComparison.Ordinal);
            Assert.Contains("'other'", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AddAgentCore_FailsWhenTwoModerationAdaptersAnswerToOneKind()
        {
            ConfigurationLoadException error = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await BuildAsync(
                    ModeratedYaml,
                    options => options.UseModeration(
                        new FakeModerationAdapter("test", new AlwaysFlagsEvaluator()),
                        new FakeModerationAdapter("test", new AlwaysFlagsEvaluator()))));

            // Two adapters for one kind means the document silently picked whichever was registered
            // first, and every seam refuses that.
            Assert.Contains("two adapters", error.Message, StringComparison.Ordinal);

            // And the noun is this seam's own. VendorSeam.Plural exists to keep four seams' wording
            // through one shared selector, so moderation's "endpoints" is pinned here — without this,
            // the argument could be dropped and nothing would fail.
            Assert.Contains("endpoints", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AddAgentCore_RefusesATurnTheDocumentsModerationVendorFlags()
        {
            using StartedHost provider = await BuildAsync(
                ModeratedYaml,
                options => options.UseModeration(new FakeModerationAdapter("test", new AlwaysFlagsEvaluator())));

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("conversation-1");
            TurnResult result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            // The wiring reaches the turn loop, and not only the registry.
            Assert.Equal(AgentCoreConfiguration.DefaultRefusalReply, result.ReplyText);
        }

        [Fact]
        public async Task AddAgentCore_TakesTheSampleRateTheDocumentSets()
        {
            using StartedHost provider = await BuildAsync(TunedYaml);

            EvaluationSampler sampler = provider.GetRequiredService<EvaluationSampler>();

            // The rate comes from evaluation.sampleRate, and the composition root reads it.
            Assert.Equal(1, sampler.Rate);
            Assert.True(sampler.ShouldSample());
        }

        // The spoken fallback, from the document to the caller.
        [Fact]
        public async Task AQuietTurn_SpeaksTheFallbackTheDocumentNames()
        {
            using StartedHost provider = await BuildAsync(TunedYaml, options => options.UseChatClients(
                _ => new RoutingChatClientFactory(new FragmentingChatClient(string.Empty))));
            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create();

            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Equal("One moment please. I will try that again.", turn.ReplyText);
            Assert.NotEqual(ConversationSession.FallbackReply, turn.ReplyText);
        }

        [Fact]
        public async Task AQuietTurn_SpeaksTheDefaultFallbackWhenTheDocumentNamesNone()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.UseChatClients(
                _ => new RoutingChatClientFactory(new FragmentingChatClient(string.Empty))));
            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create();

            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
        }

        [Fact]
        public async Task AddAgentCore_KeepsAnEvaluationServiceTheHostRegisteredFirst()
        {
            EvaluationSampler mine = new(rate: 1);
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            _ = builder.Services.AddSingleton(mine);
            ConfigureServices(builder.Services, OneAgentYaml, null);

            using StartedHost provider = await StartAsync(builder.Build());

            // The in-memory publisher grows without a bound, and a long-running host replaces it. Every
            // registration therefore steps aside, exactly as the session store does.
            Assert.Same(mine, provider.GetRequiredService<EvaluationSampler>());
        }


        // The same agent, and a document that gives the titler a model of its own.
        private const string TitlerYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        titler:
          model: { ref: titles }
        {{MinimalProviders}}
            - { kind: openai, model: gpt-4.1-nano, as: titles }
        entries:
          main:
            agent: only
        """;

        [Fact]
        public async Task AddAgentCore_GivesTheTitlerTheModelTheDocumentNames()
        {
            RecordingChatClientFactory factory = new();
            using StartedHost provider = await BuildAsync(TitlerYaml, options => options.UseChatClients(_ => factory));

            IConversationTitler titler = provider.GetRequiredService<IConversationTitler>();

            _ = Assert.IsType<ChatConversationTitler>(titler);
            Assert.Equal("titles", factory.Asked?.Ref);
        }

        [Fact]
        public async Task AddAgentCore_GivesTheTitlerTheDefaultModelWhenTheDocumentNamesNone()
        {
            RecordingChatClientFactory factory = new();
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.UseChatClients(_ => factory));

            _ = provider.GetRequiredService<IConversationTitler>();

            // A null reference is how the factory is asked for the first declared entry.
            Assert.Null(factory.Asked);
        }

        [Fact]
        public async Task AddAgentCore_KeepsATitlerTheHostRegisteredFirst()
        {
            SilentConversationTitler mine = new();
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            _ = builder.Services.AddSingleton<IConversationTitler>(mine);
            ConfigureServices(builder.Services, TitlerYaml, null);

            using StartedHost provider = await StartAsync(builder.Build());

            Assert.Same(mine, provider.GetRequiredService<IConversationTitler>());
        }

        /// <summary>A titler that names nothing, for the test that only asks who won the registration.</summary>
        private sealed class SilentConversationTitler : IConversationTitler
        {
            public async IAsyncEnumerable<string> GenerateAsync(
                string conversationId,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public async IAsyncEnumerable<string> GenerateFromAsync(
                string conversationId,
                IReadOnlyList<ChatMessage> messages,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }
        }


        // The same agent, served by a conversation-store vendor the host registers itself.
        private const string VendorTranscriptYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
          conversations: { kind: test }
        entries:
          main:
            agent: only
        """;

        // The same agent, served by a knowledge vendor the host registers itself.
        private const string VendorKnowledgeYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
          knowledge: { kind: test, collection: manuals, fields: { body: body } }
        entries:
          main:
            agent: only
        """;

        // The conversation store opens before the moderation vendor is built, so a
        // document that names both puts a failure strictly after an open. Nothing else in the boot has
        // that shape.
        private const string ConversationStoreThenModerationFailureYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
          conversations: { kind: test }
          moderation: { kind: test }
        entries:
          main:
            agent: only
        """;

        [Fact]
        public async Task ADocumentThatNamesNoConversationStoreProvider_StillOpensTheMemoryStore()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            // The turn loop writes the words of every conversation whatever a document says, so this seam has a
            // working default rather than a null, and a first run needs no database.
            Assert.NotNull(provider.GetService<IConversationStore>());
            _ = Assert.IsType<InMemoryConversationStore>(provider.GetRequiredService<Conversations>().Store);
        }

        [Fact]
        public async Task AConversationStoreVendorTheDocumentNames_IsTheStoreTheTurnWritesTo()
        {
            RecordingConversationStore store = new();
            using StartedHost provider = await BuildAsync(
                VendorTranscriptYaml,
                options => options.UseConversationStores(new TestConversationStoreAdapter(store)));

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("conversation-1");
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            // The host lists its vendors once and providers.conversations.kind picks one. Nothing but the
            // document decides where the words of a conversation land.
            Assert.Same(store, provider.GetRequiredService<Conversations>().Store);
            Assert.Equal(["user", "assistant"], store.Roles);
        }

        [Fact]
        public async Task AConversationStoreKindThisHostDoesNotRegister_FailsTheStart()
        {
            // A document that asked for something this host cannot give fails while the host starts, and
            // never on a conversation.
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(VendorTranscriptYaml));

            Assert.Contains("test", failure.Message, StringComparison.Ordinal);
        }

        /// <summary>A conversation-store vendor that hands over the store the test holds.</summary>
        private sealed class TestConversationStoreAdapter(RecordingConversationStore store) : IConversationStoreAdapter
        {
            public string Kind => "test";

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry,
                ISecretResolverPort? secrets,
                CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IConversationStore>(store);
            }
        }

        /// <summary>A conversation store that keeps the role of every row it accepted.</summary>
        private sealed class RecordingConversationStore() : DelegatingConversationStore(new InMemoryConversationStore()), IAsyncDisposable
        {
            private readonly Lock _gate = new();
            private readonly List<string> _roles = [];

            /// <summary>Gets whether this store was closed.</summary>
            public bool Closed { get; private set; }

            /// <summary>Gets the role of each row this store accepted, in the order it arrived.</summary>
            public IReadOnlyList<string> Roles
            {
                get
                {
                    lock (_gate)
                    {
                        return [.. _roles];
                    }
                }
            }

            public override ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId,
                IReadOnlyList<ConversationMessageDraft> messages,
                ConversationSessionState? state = null,
                CancellationToken cancellationToken = default)
            {
                lock (_gate)
                {
                    _roles.AddRange(messages.Select(message => message.Content.Role.Value));
                }

                IReadOnlyList<ConversationMessage> rows = [.. messages.Select(
                    (message, index) => new ConversationMessage(conversationId, index, message.TurnIndex ?? 0, message.Content, message.MessageId))];
                return ValueTask.FromResult(rows);
            }

            public override ValueTask RewriteAsync(
                string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
            {
                return ValueTask.CompletedTask;
            }

            public override ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult(0);
            }

            public ValueTask DisposeAsync()
            {
                Closed = true;
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>A knowledge vendor that hands back one port and keeps a reference to it.</summary>
        private sealed class DisposeTrackingKnowledgeAdapter : IKnowledgeStoreAdapter
        {
            public string Kind => "test";

            public bool CanServeSearch => true;

            public bool CanScope => true;

            /// <summary>Gets the port the last build returned, or <see langword="null"/> before one built.</summary>
            public DisposeTrackingKnowledgePort? Built { get; private set; }

            public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
                KnowledgeProviderConfiguration entry,
                ISecretResolverPort? secrets,
                IEmbeddingGenerator<string, Embedding<float>>? embeddings,
                bool requireScope,
                CancellationToken cancellationToken = default)
            {
                Built = new DisposeTrackingKnowledgePort();
                return ValueTask.FromResult<IKnowledgeRetrievalPort>(Built);
            }
        }

        /// <summary>A knowledge port that answers with nothing and tracks whether it was closed.</summary>
        /// <summary>A knowledge port that also reads whole cards by an exact facet value.</summary>
        private sealed class FacetCapablePort : IKnowledgeRetrievalPort, IKnowledgeFacetReadPort
        {
            public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
                string query, KnowledgeScope? scope = null, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
            }

            public ValueTask<IReadOnlyList<KnowledgeCard>> ReadByFacetAsync(
                string path, string value, int limit, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
            }
        }

        private sealed class DisposeTrackingKnowledgePort : IKnowledgeRetrievalPort, IDisposable
        {
            /// <summary>Gets whether this port was closed.</summary>
            public bool Closed { get; private set; }

            public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
                string query, KnowledgeScope? scope = null, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
            }

            public void Dispose()
            {
                Closed = true;
            }
        }

        [Fact]
        public async Task NoLoggerAndNoAuditVendor_StillRunsATurn()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create();
            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal("hello", turn.ReplyText);
        }

        [Fact]
        public async Task ABadDocument_FailsTheStartAndNamesTheFault()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(BrokenYaml));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
            Assert.Equal("/entries/main/policy/stages/0/to/0/stage", error.Pointer);
            Assert.Contains("'nowhere' is not declared", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The reference pass runs after discovery, against what the tool registry actually serves. A
        /// state slot's <c>from:</c> naming a tool nothing serves — not declared, and no <c>mcp:</c>
        /// server offers it either — still stops the boot rather than leaving the slot silently unfilled.
        /// </summary>
        [Fact]
        public async Task AnUndeclaredToolInAStateSlot_FailsTheStartAndNamesTheTool()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(UndeclaredToolYaml));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
            Assert.Equal("/state/orderStatus/from", error.Pointer);
            Assert.Contains("lookup_order", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AnAgentToolReferencingAnIdNothingServes_FailsTheStartNamingTheAgent()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(UndeclaredAgentToolYaml));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
            Assert.Equal("/agents/items/0/tools/0", error.Pointer);
            Assert.Contains("no_such_tool", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A source's own discovery can succeed while the boot still fails later: here, an agent's
        /// <c>tools:</c> names an id nothing serves, so <c>ValidateToolReferences</c> throws after
        /// <see cref="ToolRegistryStartup.BuildAsync"/> already returned. <c>AgentCoreBoot</c> tracked
        /// the source as it was built, before any discovery ran, so the failed start closes it however
        /// far the boot had got.
        /// </summary>
        [Fact]
        public async Task AToolReferenceFailureAfterDiscoverySucceeds_StillDisposesTheSource()
        {
            DisposeTrackingToolSource source = new();

            _ = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
                UndeclaredAgentToolYaml,
                options => options.AddToolSource(_ => source)));

            Assert.True(source.Disposed);
        }

        /// <summary>
        /// The conversation store is opened before the moderation vendor is built. A
        /// document that names a moderation kind this host does not register therefore fails with the
        /// store already open, and nothing between the two has taken ownership of it.
        /// </summary>
        [Fact]
        public async Task AFailureAfterTheConversationStoreOpens_StillClosesTheStore()
        {
            RecordingConversationStore store = new();

            _ = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
                ConversationStoreThenModerationFailureYaml,
                options => options
                    .UseConversationStores(new TestConversationStoreAdapter(store))
                    .UseModeration(new FakeModerationAdapter("other", new AlwaysFlagsEvaluator()))));

            Assert.True(store.Closed);
        }

        /// <summary>
        /// The id collision between a discovered tool and a declared <c>kind: agent</c> tool is only
        /// found once every source has answered, so the source that served the colliding id is open by
        /// then. It must not be left running.
        /// </summary>
        [Fact]
        public async Task AnIdCollisionFoundAfterDiscovery_StillClosesTheSource()
        {
            DisposeTrackingToolSource source = new("shared_id");

            _ = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
                CollidingAgentToolYaml,
                options => options.AddToolSource(_ => source)));

            Assert.True(source.Disposed);
        }

        /// <summary>
        /// An id no <c>tools:</c> entry names, served only by a discovering source, still satisfies an
        /// agent's reference through <see cref="AgentCoreServiceCollectionExtensions.AddAgentCore"/>
        /// end to end, public API only. An <c>mcp:</c> server's tools work exactly this way: the
        /// reference pass must resolve against what got discovered, not just what got declared.
        /// </summary>
        [Fact]
        public async Task ADiscoveredOnlyTool_SatisfiesAnAgentsReferenceThroughTheRealBoot()
        {
            using StartedHost provider = await BuildAsync(
                DiscoveredOnlyToolYaml,
                options => options.AddToolSource(_ => new DiscoveredOnlyToolSource("discovered_only")));

            Assert.NotNull(provider.GetRequiredService<IReadOnlyDictionary<string, CompiledAgent>>()["main"]);
            Assert.True(provider.GetRequiredService<ToolRegistry>().Contains("discovered_only"));
        }

        /// <summary>
        /// <see cref="ToolRegistryBuilder.VerifyEveryDeclarationIsServed"/> carves <see cref="ToolKind.Agent"/>
        /// out of its own "every declaration is served" rule, because that kind reaches no source — the
        /// compile table builds it once the agent it names has compiled. The reference pass in the
        /// composition root has to carve the same kind out of its own served-ids set for the same reason,
        /// or a document exactly like this one — an agent-as-tool — fails to
        /// boot even though it declares nothing wrong.
        /// </summary>
        [Fact]
        public async Task ADelegatingAgentTool_BootsBecauseKindAgentReachesNoSource()
        {
            using StartedHost provider = await BuildAsync(DelegatingAgentToolYaml);

            Assert.NotNull(provider.GetRequiredService<IReadOnlyDictionary<string, CompiledAgent>>()["main"]);
        }

        /// <summary>
        /// A YAML typo must never cost a round trip to an MCP server.
        /// This document carries both a structural defect and an <c>mcp:</c> server whose command does
        /// not exist, so the two possible orderings are observably different: structure-first reports
        /// the policy fault and never touches the server; discovery-first would instead report that the
        /// server could not be reached, because <c>Process.Start</c> on a missing executable fails
        /// synchronously, well before any structural error would ever be found. The error alone only
        /// infers the order; <see cref="SpyToolSource"/> observes it directly by recording whether
        /// <c>ProvideAsync</c> was ever called on any source at all — structure-first means
        /// <see cref="Tools.ToolRegistryBuilder.BuildAsync"/> never runs, so nothing
        /// is ever asked, not even a source that serves nothing.
        /// </summary>
        [Fact]
        public async Task AddAgentCore_TheStructuralFaultSurfaces_BeforeMcpIsEverAsked()
        {
            bool asked = false;

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
                StructuralFaultPlusUnreachableMcpYaml,
                options =>
                {
                    // Registered before McpToolSource, so this is asked first if discovery runs at all —
                    // a true observer of whether ToolRegistryBuilder.BuildAsync began, not just of
                    // whether the MCP source in particular got asked.
                    _ = options.AddToolSource(_ => new SpyToolSource(() => asked = true));
                    _ = options.AddToolSource(startup => new McpToolSource(startup.Secrets));
                }));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
            Assert.Equal("/entries/main/policy/stages/0/to/0/stage", error.Pointer);
            Assert.Contains("'nowhere' is not declared", error.Message, StringComparison.Ordinal);

            // Distinguishes the orders directly: an MCP connection failure would name the server id.
            Assert.DoesNotContain("bogus-server", failure.Message, StringComparison.Ordinal);

            Assert.False(asked);
        }

        [Fact]
        public async Task ADocumentThatDoesNotParse_FailsTheStart()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => StartBareAsync(options =>
                {
                    options.ConfigurationPath = "no-such-extension.txt";
                    _ = options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
                }));

            Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
        }

        [Fact]
        public async Task NoDocumentAtAll_FailsTheStartAndSaysWhatToSet()
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => StartBareAsync(
                    options => options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")))));

            Assert.Contains("names no document", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TwoDocuments_FailTheStart()
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => StartBareAsync(options =>
                {
                    options.Configuration = ConfigurationLoader.LoadYaml(OneAgentYaml);
                    options.ConfigurationPath = "config/example.yaml";
                    _ = options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
                }));

            Assert.Contains("names two documents", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task NoChatClientAdapter_FailsTheStart()
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => StartBareAsync(options => options.Configuration = ConfigurationLoader.LoadYaml(OneAgentYaml)));

            Assert.Contains("UseChatClients", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AddAgentCore_AnAgentWithSkills_Boots()
        {
            using SkillFolder folder = SkillFolder.Create().WithSkill("warranty-returns");

            const string yaml = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: only, instructions: "I answer everything" }
            entries:
              main:
                agent: only
            """;

            using StartedHost host = await BuildAsync(yaml, options => options.UseSkills(folder.Root));

            Assert.NotNull(host);
        }

        [Fact]
        public async Task AddAgentCore_AnAgentNamingASkillTheFolderDoesNotServe_FailsAtBoot()
        {
            using SkillFolder folder = SkillFolder.Create().WithSkill("warranty-returns");

            const string yaml = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: support, skills: [warranty-return] }
            entries:
              main:
                agent: support
            """;

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(yaml, options => options.UseSkills(folder.Root)));

            Assert.Contains("warranty-return", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AddAgentCore_ClosesTheSkillsSourceWhenTheHostShutsDown()
        {
            using SkillFolder folder = SkillFolder.Create().WithSkill("warranty-returns");

            const string yaml = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: support, skills: [warranty-returns] }
            entries:
              main:
                agent: support
            """;

            TrackingSkillsSource tracked = new(folder.Root);

            StartedHost host = await BuildAsync(yaml, options => options.UseSkills(tracked));
            host.Dispose();

            Assert.True(tracked.Disposed);
        }

        /// <summary>
        /// The source has to be open for the document to be checked against the names it serves, so a
        /// <c>skills:</c> entry nothing serves fails with it open. <c>AgentCoreBoot</c> takes ownership
        /// before running that check, and this is what holds the two lines in that order.
        /// </summary>
        [Fact]
        public async Task ASkillReferenceFailure_StillClosesTheSkillsSource()
        {
            using SkillFolder folder = SkillFolder.Create().WithSkill("warranty-returns");

            const string yaml = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: support, skills: [warranty-return] }
            entries:
              main:
                agent: support
            """;

            TrackingSkillsSource tracked = new(folder.Root);

            _ = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(yaml, options => options.UseSkills(tracked)));

            Assert.True(tracked.Disposed);
        }

        /// <summary>
        /// The reserved-tool-id check reads only the document, so it runs outside the null check and
        /// would sit happily above <c>Track</c> — where its refusal would strand an open source. This
        /// is what holds it below.
        /// </summary>
        [Fact]
        public async Task AReservedSkillToolIdFailure_StillClosesTheSkillsSource()
        {
            using SkillFolder folder = SkillFolder.Create().WithSkill("warranty-returns");

            const string yaml = """
            apiVersion: agentcore/v1
            tools:
              - id: load_skill
                kind: binding
                binds: CreateCase
                description: Open a service case for a human agent.
                parameters:
                  type: object
                  properties: { summary: { type: string } }
                  required: [ summary ]
            agents:
              items:
                - { id: support, skills: [warranty-returns], tools: [ load_skill ] }
            entries:
              main:
                agent: support
            """;

            TrackingSkillsSource tracked = new(folder.Root);

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(
                    yaml,
                    options =>
                    {
                        _ = options.UseSkills(tracked);
                        _ = options.Bind("CreateCase", (_, _) => ValueTask.FromResult<object?>(new JsonObject()));
                    }));

            Assert.Contains("reserved", failure.Message, StringComparison.Ordinal);
            Assert.True(tracked.Disposed);
        }

        /// <summary>Composes the guarded graph over one offline model for each node.</summary>
        /// <returns>The provider a test resolves from.</returns>
        private static Task<StartedHost> BuildGuardedGraphAsync()
        {
            RoutingChatClientFactory models = new(new FragmentingChatClient("ROUTED"));
            _ = models.Route("human", new FragmentingChatClient("ESCALATED"));
            _ = models.Route("bot", new FragmentingChatClient("HANDLED"));

            return BuildAsync(GuardedGraphYaml, options => options.UseChatClients(_ => models));
        }

        private static async Task<(IHost Host, FlushRecordingTelemetryAdapter Adapter)> BuildTelemetryHostAsync()
        {
            FlushRecordingTelemetryAdapter adapter = new("test");
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            ConfigureServices(
                builder.Services,
                TelemetryYaml,
                options => options.UseTelemetry(adapter));

            return (builder.Build(), adapter);
        }

        /// <summary>An adapter that starts nothing and hands back a session that records its flush.</summary>
        private sealed class FlushRecordingTelemetryAdapter(string kind) : ITelemetryAdapter
        {
            public string Kind => kind;

            public FlushRecordingSession Session { get; } = new();

            public ValueTask<ITelemetrySession> StartAsync(
                TelemetryProviderConfiguration entry,
                ISecretResolverPort? secrets,
                CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<ITelemetrySession>(Session);
            }
        }

        /// <summary>A session that exports nowhere and counts how many times it was drained.</summary>
        private sealed class FlushRecordingSession : ITelemetrySession
        {
            public ILoggerProvider? Logs => null;

            public int Flushes { get; private set; }

            public ValueTask DisposeAsync()
            {
                Flushes++;
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>A moderation vendor a test registers, which counts the times it was asked to build.</summary>
        private sealed class FakeModerationAdapter(string kind, IEvaluator evaluator) : IModerationAdapter
        {
            public string Kind => kind;

            /// <summary>Gets the number of times the composition root asked this vendor to build.</summary>
            public int Builds { get; private set; }

            public ValueTask<IEvaluator> CreateAsync(
                VendorProviderConfiguration entry,
                ISecretResolverPort? secrets,
                CancellationToken cancellationToken = default)
            {
                Builds++;
                return ValueTask.FromResult(evaluator);
            }
        }

        /// <summary>A moderation evaluator that flags every text, so the wiring is observable.</summary>
        private sealed class AlwaysFlagsEvaluator : IEvaluator
        {
            public IReadOnlyCollection<string> EvaluationMetricNames => ["Content Safety"];

            public ValueTask<EvaluationResult> EvaluateAsync(
                IEnumerable<ChatMessage> messages,
                ChatResponse modelResponse,
                ChatConfiguration? chatConfiguration = null,
                IEnumerable<EvaluationContext>? additionalContext = null,
                CancellationToken cancellationToken = default)
            {
                BooleanMetric metric = new("Content Safety", value: false);
                metric.AddOrUpdateContext(new ModerationVerdict(flagged: true, ["harassment"]));
                return ValueTask.FromResult(new EvaluationResult(metric));
            }
        }

        /// <summary>Sessions a host registers in place of the default ones.</summary>
        private sealed class CountingConversationSessions : IConversationSessions
        {
            public ValueTask<ConversationSession> GetOrOpenAsync(string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public ValueTask<ConversationSession?> TryGetAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<ConversationSession?>(null);
            }

            public ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// A tool source that serves one id no document ever declares in <c>tools:</c>, standing in for
        /// what an MCP server's discovery would supply. <see cref="ToolRegistryBuilder"/> imposes no rule
        /// that a served id be declared, so this alone is enough to prove the reference pass runs against
        /// what got discovered.
        /// </summary>
        private sealed class DiscoveredOnlyToolSource(string id) : IToolSource
        {
            public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
                ToolSourceContext context, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                                [new ToolRegistration(id, "A tool discovered but never declared.", () => AIFunctionFactory.Create(() => "ok", id))]);
            }
        }

        /// <summary>A tool source that serves nothing, and tells a test when it was asked to.</summary>
        private sealed class SpyToolSource(Action onProvide) : IToolSource
        {
            public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
                ToolSourceContext context, CancellationToken cancellationToken = default)
            {
                onProvide();
                return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>([]);
            }
        }

        /// <summary>A tool source a host registers, which records whether it was ever closed.</summary>
        /// <param name="servedId">One id to serve, or <see langword="null"/> to serve nothing.</param>
        private sealed class DisposeTrackingToolSource(string? servedId = null) : IToolSource, IAsyncDisposable
        {
            /// <summary>Gets whether this source was disposed.</summary>
            public bool Disposed { get; private set; }

            public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
                ToolSourceContext context, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                                servedId is null
                                    ? []
                                    : [new ToolRegistration(servedId, "A tool discovered under a claimed id.", () => AIFunctionFactory.Create(() => "ok", servedId))]);
            }

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A file source that records whether the boot tracker closed it.</summary>
    internal sealed class TrackingSkillsSource(string path) : AgentSkillsSource
    {
        private readonly AgentFileSkillsSource _inner = new(path);

        public bool Disposed { get; private set; }

        public override Task<IList<AgentSkill>> GetSkillsAsync(
            AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
        {
            return _inner.GetSkillsAsync(context, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

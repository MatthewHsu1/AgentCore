using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// The composition root. It loads, validates, resolves, compiles, and registers, in that order.
/// </summary>
/// <remarks>
/// A configuration defect stops the host here and never on the first call. Every test proves that by
/// calling AddAgentCoreAsync alone, with no request anywhere.
/// </remarks>
public sealed class AddAgentCoreTests
{
    private const string OneAgentYaml =
        """
        apiVersion: agentcore/v1
        name: composed
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // The same agent, and a document that names a moderation vendor.
    private const string ModeratedYaml =
        """
        apiVersion: agentcore/v1
        name: composed
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
          moderation: { kind: test }
        """;

    // The same agent, served by a vendor this host's fake adapter does not answer to.
    private const string OtherVendorYaml =
        """
        apiVersion: agentcore/v1
        name: other-vendor
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: anthropic, model: claude-sonnet-5, as: reply }
        """;

    // The same agent, with both tunable keys of the document set away from their default.
    private const string TunedYaml =
        """
        apiVersion: agentcore/v1
        name: tuned
        fallbackReply: "One moment please. I will try that again."
        evaluation:
          sampleRate: 1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    private const string BindingYaml =
        """
        apiVersion: agentcore/v1
        name: with-binding
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
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // Both built-in tools, so one document reaches both knowledge ports.
    private const string KnowledgeYaml =
        """
        apiVersion: agentcore/v1
        name: with-knowledge
        tools:
          - { id: search_chunks, kind: builtin, uses: knowledge.search }
          - { id: read_doc,      kind: builtin, uses: knowledge.read }
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ search_chunks, read_doc ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // Only the tool the document store answers, so a host that binds no retrieval adapter starts.
    private const string ReadOnlyKnowledgeYaml =
        """
        apiVersion: agentcore/v1
        name: with-document-store
        tools:
          - { id: read_doc, kind: builtin, uses: knowledge.read }
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ read_doc ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // Both built-in tools, with a different vendor named for each knowledge port.
    private const string TwoKnowledgeVendorsYaml =
        """
        apiVersion: agentcore/v1
        name: with-two-knowledge-vendors
        tools:
          - { id: search_chunks, kind: builtin, uses: knowledge.search }
          - { id: read_doc,      kind: builtin, uses: knowledge.read }
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ search_chunks, read_doc ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
          knowledge:
            search: fake-ranker
            documents: fake-reader
        """;

    // Both built-in tools, with one vendor behind both knowledge ports.
    private const string OneKnowledgeVendorYaml =
        """
        apiVersion: agentcore/v1
        name: with-one-knowledge-vendor
        tools:
          - { id: search_chunks, kind: builtin, uses: knowledge.search }
          - { id: read_doc,      kind: builtin, uses: knowledge.read }
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ search_chunks, read_doc ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
          knowledge:
            search: fake-store
            documents: fake-store
        """;

    // Both built-in tools, with a ranking vendor the host registers and a document kind it does not.
    private const string RankerOnlyKnowledgeYaml =
        """
        apiVersion: agentcore/v1
        name: with-a-ranker-only
        tools:
          - { id: search_chunks, kind: builtin, uses: knowledge.search }
          - { id: read_doc,      kind: builtin, uses: knowledge.read }
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ search_chunks, read_doc ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
          knowledge:
            search: fake-ranker
            documents: filesystem
        """;

    private const string SecretYaml =
        """
        apiVersion: agentcore/v1
        name: with-secret
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
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // Row 4 of the section 8.2 compile table, with a guarded edge on each exit of the start node.
    // Check 5 proves the two guards exclusive, so exactly one edge fires for each call.
    private const string GuardedGraphYaml =
        """
        apiVersion: agentcore/v1
        name: guarded-composed
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
        graph:
          nodes:
            - { id: route, agent: router, start: true }
            - { id: escalated, agent: human, output: true }
            - { id: handled, agent: bot, output: true }
          edges:
            - { from: route, to: escalated, when: wants_human }
            - { from: route, to: handled, when: stays_with_bot }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: router }
            - { kind: openai, model: gpt-4.1-mini, as: human }
            - { kind: openai, model: gpt-4.1-mini, as: bot }
        """;

    // A stage names a target that policy.stages does not declare, so check 2 fails the load.
    private const string BrokenYaml =
        """
        apiVersion: agentcore/v1
        name: broken
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        policy:
          initial: start
          stages:
            - { id: start, agent: only, to: [ { stage: nowhere } ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // -------------------------------------------------------------------------------------------
    // What it registers.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AddAgentCore_RegistersTheCompiledAgentAsAProcessSingleton()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var first = provider.GetRequiredService<CompiledAgent>();
        var second = provider.GetRequiredService<CompiledAgent>();

        Assert.Same(first, second);
        Assert.Equal("composed", first.Name);

        // The registry compiled once, and every call shares that one result.
        Assert.Equal(1, provider.GetRequiredService<CompiledAgentRegistry>().CompileCount);
    }

    [Fact]
    public async Task AddAgentCore_RegistersOneSessionFactoryThatBuildsANewSessionForEachCall()
    {
        using var provider = await BuildAsync(OneAgentYaml);
        var factory = provider.GetRequiredService<ICallSessionFactory>();

        Assert.Same(factory, provider.GetRequiredService<ICallSessionFactory>());

        // A CallSession belongs to one call, so it is not a singleton and the container holds none.
        var first = factory.Create();
        var second = factory.Create();
        Assert.NotSame(first, second);
        Assert.NotEqual(first.CallId, second.CallId);
    }

    [Fact]
    public async Task AddAgentCore_RegistersTheAgentShimAsAProcessSingleton()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var agent = provider.GetRequiredService<AgentCoreAgent>();

        Assert.Same(agent, provider.GetRequiredService<AgentCoreAgent>());
        Assert.Equal("composed", agent.Name);

        // One session of the shim is one call, drawn from the same factory the rest of the host
        // uses, so the two seams describe the same calls.
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(session.GetService<CallSession>());
    }

    [Fact]
    public async Task AddAgentCore_RegistersTheInMemorySessionStoreByDefault()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var store = provider.GetRequiredService<ICallSessionStore>();

        Assert.IsType<InMemoryCallSessionStore>(store);
        Assert.Same(store, provider.GetRequiredService<ICallSessionStore>());
    }

    [Fact]
    public async Task AddAgentCore_KeepsASessionStoreTheHostRegisteredFirst()
    {
        CountingCallSessionStore mine = new();
        ServiceCollection services = new();
        services.AddSingleton<ICallSessionStore>(mine);
        await ConfigureAsync(services, OneAgentYaml, null);

        using var provider = services.BuildServiceProvider();

        // A distributed store replaces the default one, and the default steps aside.
        Assert.Same(mine, provider.GetRequiredService<ICallSessionStore>());
    }

    // -------------------------------------------------------------------------------------------
    // The document picks the vendor, and no code names one: the point of the adapter seam.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheAdapterOverload_LetsTheDocumentPickTheVendorByItsKind()
    {
        // 'kind: openai' selects the adapter registered under that kind. The host lists what it
        // supports, once, and the document decides which entry runs.
        using var provider = await BuildAsync(OneAgentYaml, options => options.UseChatClients(
            new FakeChatClientAdapter("openai", () => new SequencedChatClient("routed")),
            new FakeChatClientAdapter("anthropic", () => new SequencedChatClient("wrong vendor"))));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create();
        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal("routed", turn.ReplyText);
    }

    [Fact]
    public async Task AKindNoRegisteredAdapterServes_FailsTheStartAndNamesBothSides()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
            OtherVendorYaml,
            options => options.UseChatClients(
                new FakeChatClientAdapter("openai", () => new SequencedChatClient("hello")))));

        // The message names the kind the document wrote and the kinds the host registers, so the
        // reader knows which side to change.
        Assert.Contains("anthropic", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'openai'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAsyncSeam_BuildsItsFactoryWithNoBlockedThread()
    {
        using var provider = await BuildAsync(OneAgentYaml, options => options.UseChatClients(
            async (startup, cancellationToken) =>
            {
                await Task.Yield();
                return new RoutingChatClientFactory(new SequencedChatClient("awaited"));
            }));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create();
        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal("awaited", turn.ReplyText);
    }

    // -------------------------------------------------------------------------------------------
    // The seams the host binds.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task ABindingTool_ReachesTheDelegateTheHostRegistered()
    {
        using var provider = await BuildAsync(
            BindingYaml,
            options => options.Bind("CreateCase", (_, _) => ValueTask.FromResult<object?>(new JsonObject())));

        var bindings = provider.GetRequiredService<ToolBindingRegistry>();

        Assert.True(bindings.Contains("CreateCase"));
        Assert.Equal(1, bindings.Count);
    }

    [Fact]
    public async Task ABindingToolWithNoDelegate_FailsTheStartAndNamesTheBinding()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(BindingYaml));

        Assert.Contains("CreateCase", failure.Message, StringComparison.Ordinal);
        Assert.Contains("did not register", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneStoreThatAnswersBothPorts_BindsInOneLineAndIsBuiltOnce()
    {
        var built = 0;

        using var provider = await BuildAsync(KnowledgeYaml, options => options.UseKnowledge(_ =>
        {
            built++;
            return new EmptyKnowledgeStore();
        }));

        // Both built-in tools compiled, and the one adapter behind them was opened once.
        Assert.NotNull(provider.GetRequiredService<CompiledAgent>());
        Assert.Equal(1, built);
    }

    [Fact]
    public async Task AHostThatBindsOnlyTheDocumentStore_Starts()
    {
        // Section 7 splits the two ports so a vendor that supplies only one is enough. This host has
        // the reading half and no retrieval adapter, and knowledge.read still reaches the model.
        using var provider = await BuildAsync(
            ReadOnlyKnowledgeYaml,
            options => options.UseDocumentStore(_ => new EmptyKnowledgeStore()));

        Assert.NotNull(provider.GetRequiredService<CompiledAgent>());
    }

    [Fact]
    public async Task AHostThatBindsOnlyTheDocumentStore_FailsTheStartAndNamesTheUnboundPort()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
            KnowledgeYaml,
            options => options.UseDocumentStore(_ => new EmptyKnowledgeStore())));

        Assert.Contains("search_chunks", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IKnowledgeRetrievalPort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoAdapters_BindToTheTwoPortsApart()
    {
        EmptyKnowledgeStore retrieval = new();
        EmptyKnowledgeStore documents = new();

        using var provider = await BuildAsync(KnowledgeYaml, options => options
            .UseKnowledgeRetrieval(_ => retrieval)
            .UseDocumentStore(_ => documents));

        // This is the shape the Zilliz connector arrives in: one adapter ranks and another reads.
        Assert.NotNull(provider.GetRequiredService<CompiledAgent>());
    }

    [Fact]
    public async Task TheKnowledgeRegistry_LetsTheDocumentPickTheStoreOfEachPort()
    {
        // The host lists what it supports, once. providers.knowledge.search and
        // providers.knowledge.documents then bind the two ports apart, with no code named here.
        FakeKnowledgeStoreAdapter ranker = new("fake-ranker") { CanServeDocuments = false };
        FakeKnowledgeStoreAdapter reader = new("fake-reader") { CanServeSearch = false };

        using var provider = await BuildAsync(
            TwoKnowledgeVendorsYaml,
            options => options.UseKnowledgeStores(ranker, reader));

        await CallToolAsync(provider, "search_chunks", "query", "shipping");
        await CallToolAsync(provider, "read_doc", "documentId", "policies/shipping.md");

        Assert.Equal(["shipping"], ranker.Store.Queries);
        Assert.Equal(["policies/shipping.md"], reader.Store.Reads);
        Assert.Empty(ranker.Store.Reads);
        Assert.Empty(reader.Store.Queries);
    }

    [Fact]
    public async Task AnExplicitKnowledgeSeam_BeatsTheRegistryForThePortItSets()
    {
        FakeKnowledgeStoreAdapter registry = new("fake-store");
        RecordingKnowledgeStore mine = new();

        using var provider = await BuildAsync(OneKnowledgeVendorYaml, options => options
            .UseKnowledgeStores(registry)
            .UseKnowledgeRetrieval(_ => mine));

        await CallToolAsync(provider, "search_chunks", "query", "shipping");
        await CallToolAsync(provider, "read_doc", "documentId", "policies/shipping.md");

        // The explicit call wins the port it sets, and the registry keeps the port it does not.
        Assert.Equal(["shipping"], mine.Queries);
        Assert.Empty(registry.Store.Queries);
        Assert.Equal(["policies/shipping.md"], registry.Store.Reads);

        // The shadowed port is not built either. A vendor that opens a client on its search build
        // must not open one this host then throws away.
        Assert.Equal(0, registry.SearchBuilds);
        Assert.Equal(1, registry.DocumentBuilds);
    }

    [Fact]
    public async Task AnExplicitKnowledgeSeam_SparesTheRegistryAKindItCannotServe()
    {
        // The document reads from 'filesystem' and this host registers only the ranker, exactly as a
        // host with the Zilliz connector and a document store of its own does. The explicit call
        // answers the document port, so the registry never looks that kind up and the start holds.
        FakeKnowledgeStoreAdapter ranker = new("fake-ranker") { CanServeDocuments = false };
        RecordingKnowledgeStore mine = new();

        using var provider = await BuildAsync(RankerOnlyKnowledgeYaml, options => options
            .UseKnowledgeStores(ranker)
            .UseDocumentStore(_ => mine));

        await CallToolAsync(provider, "search_chunks", "query", "shipping");
        await CallToolAsync(provider, "read_doc", "documentId", "policies/shipping.md");

        Assert.Equal(["shipping"], ranker.Store.Queries);
        Assert.Equal(["policies/shipping.md"], mine.Reads);
        Assert.Equal(1, ranker.SearchBuilds);
        Assert.Equal(0, ranker.DocumentBuilds);
    }

    [Fact]
    public async Task ASecretReference_ResolvesOnceAtStartup()
    {
        using HttpClient client = new();
        MapSecretResolver resolver = new();
        resolver.With("orders-api-key", "a-value-no-message-repeats");

        using var provider = await BuildAsync(
            SecretYaml,
            options =>
            {
                options.SecretResolver = resolver;
                options.AddToolFactory(startup => new HttpToolFactory(client, startup.Secrets));
            });

        var secrets = provider.GetRequiredService<ResolvedSecrets>();

        Assert.True(secrets.Contains("orders-api-key"));

        // A resolved set lands in a log line sooner or later, so it reports the count and never a value.
        Assert.DoesNotContain("a-value-no-message-repeats", secrets.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretReferenceWithNoResolver_FailsTheStartAndNamesTheSecret()
    {
        using HttpClient client = new();

        var failure = await Assert.ThrowsAsync<SecretResolutionException>(() => BuildAsync(
            SecretYaml,
            options => options.AddToolFactory(startup => new HttpToolFactory(client, startup.Secrets))));

        Assert.Equal("orders-api-key", failure.SecretName);
    }

    // -------------------------------------------------------------------------------------------
    // Row 4 of the compile table, through the only supported composition root.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AGuardedGraph_Starts()
    {
        using var provider = await BuildGuardedGraphAsync();

        var compiled = provider.GetRequiredService<CompiledAgent>();

        // The document passes all eight checks and now compiles too. AddAgentCore binds the guard
        // evaluator and CallStateScope, so a guarded edge is reachable from here.
        Assert.Equal(CompiledAgentShape.ExplicitGraph, compiled.Shape);
        Assert.Equal("guarded-composed", compiled.Name);
    }

    [Theory]
    [InlineData(true, "ESCALATED", "HANDLED")]
    [InlineData(false, "HANDLED", "ESCALATED")]
    public async Task AGuardedGraph_TakesTheEdgeTheStateOfTheCallNames(bool escalate, string taken, string refused)
    {
        using var provider = await BuildGuardedGraphAsync();
        var session = provider.GetRequiredService<ICallSessionFactory>().Create();
        session.State.TryWrite("escalate", escalate);

        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Contains(taken, turn.ReplyText, StringComparison.Ordinal);
        Assert.DoesNotContain(refused, turn.ReplyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuardedGraph_KeepsTwoCallsApartWhenTheyRunAtTheSameTime()
    {
        using var provider = await BuildGuardedGraphAsync();
        var sessions = provider.GetRequiredService<ICallSessionFactory>();
        var token = TestContext.Current.CancellationToken;

        var escalated = sessions.Create();
        escalated.State.TryWrite("escalate", true);
        var handled = sessions.Create();
        handled.State.TryWrite("escalate", false);

        // One compiled graph, two calls, two edges. Neither call reads the state of the other.
        var turns = await Task.WhenAll(
            escalated.RunTurnAsync("hello", token),
            handled.RunTurnAsync("hello", token));

        Assert.Contains("ESCALATED", turns[0].ReplyText, StringComparison.Ordinal);
        Assert.Contains("HANDLED", turns[1].ReplyText, StringComparison.Ordinal);
        Assert.Equal(1, provider.GetRequiredService<CompiledAgentRegistry>().CompileCount);
    }

    // -------------------------------------------------------------------------------------------
    // The evaluation seam of D13. Triage row T18 defers the online path, so the rate is 0.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AddAgentCore_RegistersTheEvaluationSeamWithTheOnlinePathClosed()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var registry = provider.GetRequiredService<EvaluatorRegistry>();
        var sampler = provider.GetRequiredService<EvaluationSampler>();

        // D13 names fault_code, and it calls no model, so it is the one evaluator that is safe by
        // default.
        Assert.True(registry.Contains("fault_code"));

        // T18: a judge must never block a turn, and the offline gate has not proved the evaluators
        // yet. A rate of 0 draws no number and calls nothing.
        Assert.Equal(0, sampler.Rate);
        Assert.False(sampler.ShouldSample());

        Assert.IsType<InMemoryEvaluationScorePublisher>(provider.GetRequiredService<IEvaluationScorePublisher>());
    }

    [Fact]
    public async Task AddAgentCore_RegistersNoModeratorWhenTheHostBindsNoVendor()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var registry = provider.GetRequiredService<EvaluatorRegistry>();

        // A host that registers no moderation vendor moderates nothing, and every turn reaches the
        // model. Moderation needs a vendor account, and a library that refused to start without one
        // could not be used in a test.
        Assert.False(registry.Contains(PromptModerator.ModerationEvaluatorName));
        Assert.Null(PromptModerator.FromRegistry(registry));
    }

    [Fact]
    public async Task AddAgentCore_BuildsNoModeratorWhenTheDocumentNamesNoProvider()
    {
        var adapter = new FakeModerationAdapter("test", new AlwaysFlagsEvaluator());

        // The vendor is registered and the document names none, so the adapter is never asked to
        // build anything. Registering a vendor costs nothing until a document names it.
        using var provider = await BuildAsync(OneAgentYaml, options => options.UseModeration(adapter));

        Assert.False(provider.GetRequiredService<EvaluatorRegistry>()
            .Contains(PromptModerator.ModerationEvaluatorName));
        Assert.Equal(0, adapter.Builds);
    }

    [Fact]
    public async Task AddAgentCore_BuildsTheModerationVendorTheDocumentNames()
    {
        var adapter = new FakeModerationAdapter("test", new AlwaysFlagsEvaluator());

        using var provider = await BuildAsync(ModeratedYaml, options => options.UseModeration(adapter));

        var registry = provider.GetRequiredService<EvaluatorRegistry>();

        // The same object serves the turn loop and the offline golden set, which is what D13 means
        // by an evaluator written once and used twice.
        Assert.Equal(1, adapter.Builds);
        Assert.True(registry.Contains(PromptModerator.ModerationEvaluatorName));
        Assert.True(registry.Contains("fault_code"));
    }

    [Fact]
    public async Task AddAgentCore_MatchesTheModerationKindWithoutRegardToCase()
    {
        // A vendor name is written by a human, exactly as the knowledge kinds are matched.
        var adapter = new FakeModerationAdapter("TEST", new AlwaysFlagsEvaluator());

        using var provider = await BuildAsync(ModeratedYaml, options => options.UseModeration(adapter));

        Assert.Equal(1, adapter.Builds);
    }

    [Fact]
    public async Task AddAgentCore_FailsWhenTheDocumentNamesAModerationKindThisHostDoesNotRegister()
    {
        var error = await Assert.ThrowsAsync<ConfigurationLoadException>(
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
        var error = await Assert.ThrowsAsync<ConfigurationLoadException>(
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
        using var provider = await BuildAsync(
            ModeratedYaml,
            options => options.UseModeration(new FakeModerationAdapter("test", new AlwaysFlagsEvaluator())));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create("call-1");
        var result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        // The wiring reaches the turn loop, and not only the registry.
        Assert.Equal(AgentCoreConfiguration.DefaultRefusalReply, result.ReplyText);
    }

    [Fact]
    public async Task AddAgentCore_TakesTheSampleRateTheDocumentSets()
    {
        using var provider = await BuildAsync(TunedYaml);

        var sampler = provider.GetRequiredService<EvaluationSampler>();

        // T18: the rate comes from evaluation.sampleRate, and the composition root reads it.
        Assert.Equal(1, sampler.Rate);
        Assert.True(sampler.ShouldSample());
    }

    // -------------------------------------------------------------------------------------------
    // The spoken fallback of section 8.7, from the document to the caller.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AQuietTurn_SpeaksTheFallbackTheDocumentNames()
    {
        using var provider = await BuildAsync(TunedYaml, options => options.UseChatClients(
            _ => new RoutingChatClientFactory(new SequencedChatClient(string.Empty))));
        var session = provider.GetRequiredService<ICallSessionFactory>().Create();

        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal("One moment please. I will try that again.", turn.ReplyText);
        Assert.NotEqual(CallSession.FallbackReply, turn.ReplyText);
    }

    [Fact]
    public async Task AQuietTurn_SpeaksTheDefaultFallbackWhenTheDocumentNamesNone()
    {
        using var provider = await BuildAsync(OneAgentYaml, options => options.UseChatClients(
            _ => new RoutingChatClientFactory(new SequencedChatClient(string.Empty))));
        var session = provider.GetRequiredService<ICallSessionFactory>().Create();

        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal(CallSession.FallbackReply, turn.ReplyText);
    }

    [Fact]
    public async Task AddAgentCore_KeepsAnEvaluationServiceTheHostRegisteredFirst()
    {
        EvaluationSampler mine = new(rate: 1);
        ServiceCollection services = new();
        services.AddSingleton(mine);
        await ConfigureAsync(services, OneAgentYaml, null);

        using var provider = services.BuildServiceProvider();

        // The in-memory publisher grows without a bound, and a long-running host replaces it. Every
        // registration therefore steps aside, exactly as the session store does.
        Assert.Same(mine, provider.GetRequiredService<EvaluationSampler>());
    }

    // -------------------------------------------------------------------------------------------
    // The audit sink: named by providers.audit, and never absent.
    // -------------------------------------------------------------------------------------------

    // The same agent, and a document that names the built-in memory kind on purpose.
    private const string MemoryAuditYaml =
        """
        apiVersion: agentcore/v1
        name: composed
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
          audit: { kind: memory }
        """;

    // The same agent, served by an audit vendor the host registers itself.
    private const string VendorAuditYaml =
        """
        apiVersion: agentcore/v1
        name: composed
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
          audit: { kind: test }
        """;

    [Fact]
    public async Task TheDefaultAuditSink_ReachesTheTurnLoopAndTheChainVerifies()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var session = provider.GetRequiredService<ICallSessionFactory>().Create("call-1");
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // The queue is what keeps the append off the turn, so the rows land on a thread of their own
        // and a reader that wants them now asks for them now.
        await Queue(provider).FlushAsync(TestContext.Current.CancellationToken);

        var events = Sink(provider).EventsOf("call-1");
        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.TurnCompleted],
            events.Select(item => item.Kind).ToArray());
        Assert.True(AuditChain.Verify(AuditChain.LinkAll(events)).IsIntact);
    }

    [Fact]
    public async Task ADocumentThatNamesNoAuditProvider_StillOpensTheMemorySink()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        // The turn loop produces the events of D23 whatever a document says, so the seam that receives
        // them has a working default rather than a null. That is what lets every reading of a call be
        // unconditional, and what lets a first run and a test work with no database.
        Assert.NotNull(provider.GetService<IAuditSinkPort>());
        Assert.NotNull(provider.GetService<InMemoryAuditSink>());
    }

    [Fact]
    public async Task ADocumentThatNamesTheMemoryKind_OpensTheSameSinkAsNamingNothing()
    {
        using var provider = await BuildAsync(MemoryAuditYaml);

        // memory is this library's own name and it needs no registered vendor, so writing it says out
        // loud what leaving the block out does quietly. The startup warning is the difference.
        Assert.NotNull(provider.GetService<InMemoryAuditSink>());
    }

    [Fact]
    public async Task AnAuditVendorTheDocumentNames_IsTheStoreBehindTheQueue()
    {
        RecordingAuditSink store = new();
        using var provider = await BuildAsync(
            VendorAuditYaml,
            options => options.UseAuditSinks(new TestAuditSinkAdapter(store)));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create("call-1");
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
        await Queue(provider).FlushAsync(TestContext.Current.CancellationToken);

        // The host lists its vendors once and providers.audit.kind picks one, exactly as the five
        // seams beside it. Nothing but the document decides which store the chain lands in.
        Assert.Same(store, provider.GetRequiredService<RecordingAuditSink>());
        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.TurnCompleted],
            store.Events.Select(item => item.Kind).ToArray());
    }

    [Fact]
    public async Task AnAuditKindThisHostDoesNotRegister_FailsTheStart()
    {
        // A document that asked for something this host cannot give fails while the host starts, and
        // never on a call. The message names the kind, exactly as every other vendor seam.
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(VendorAuditYaml));

        Assert.Contains("test", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAuditSink_IsWrappedInTheQueueThatKeepsItOffTheTurn()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        // Section 7 puts a durable insert at 13 ms p50 against 91 nanoseconds to enqueue, and the rule
        // that follows is applied once, here, to whatever the document opened. So an adapter that
        // blocks on its database is a correct adapter, and no adapter carries a queue of its own.
        Assert.IsType<QueuedAuditSink>(provider.GetRequiredService<IAuditSinkPort>());
    }

    /// <summary>Reads back the queue the composition root put in front of the document's store.</summary>
    private static QueuedAuditSink Queue(IServiceProvider provider)
        => Assert.IsType<QueuedAuditSink>(provider.GetRequiredService<IAuditSinkPort>());

    /// <summary>Reads back the store itself, which is registered under its own concrete type.</summary>
    /// <remarks>
    /// The document builds the store now, not the host, so this is how a test that wants the events
    /// of one call reaches the thing that holds them. Resolving <see cref="IAuditSinkPort"/> gives the
    /// queue instead, because that is the only registration that honours the port's contract.
    /// </remarks>
    private static InMemoryAuditSink Sink(IServiceProvider provider)
        => provider.GetRequiredService<InMemoryAuditSink>();

    [Fact]
    public async Task NoLoggerAndNoAuditVendor_StillRunsATurn()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var session = provider.GetRequiredService<ICallSessionFactory>().Create();
        var turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal("hello", turn.ReplyText);
    }

    // -------------------------------------------------------------------------------------------
    // The host's own observers: the socket behind ICallObserver.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AHostObserver_ReadsTheFactsOfATurn()
    {
        RecordingCallObserver first = new();
        RecordingCallObserver second = new();
        using var provider = await BuildAsync(OneAgentYaml, options => options.UseObservers(first, second));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create("call-1");
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // The port is public, so a host writes one of these and binds it. Every observer of a call
        // reads the same facts, and the library's own three are neither replaced nor bypassed.
        Assert.Equal([CallEventKind.CallStarted, CallEventKind.TurnCompleted], first.Seen);
        Assert.Equal([CallEventKind.CallStarted, CallEventKind.TurnCompleted], second.Seen);
    }

    [Fact]
    public async Task AHostObserverThatThrows_CostsNeitherTheTurnNorTheChain()
    {
        using var provider = await BuildAsync(
            OneAgentYaml,
            options => options.UseObservers(new ThrowingCallObserver()));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create("call-1");
        var turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // An observer records the call and is never a part of it. That holds for the host's own, and
        // it holds for the readings registered beside it: a broken host observer does not cost the
        // chain of D23 a single row.
        Assert.Equal("hello", turn.ReplyText);

        await Queue(provider).FlushAsync(TestContext.Current.CancellationToken);

        var events = Sink(provider).EventsOf("call-1");
        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.TurnCompleted],
            events.Select(item => item.Kind).ToArray());
        Assert.True(AuditChain.Verify(AuditChain.LinkAll(events)).IsIntact);
    }

    [Fact]
    public async Task UseObserversTwice_KeepsBothRegistrations()
    {
        RecordingCallObserver first = new();
        RecordingCallObserver second = new();
        using var provider = await BuildAsync(
            OneAgentYaml,
            options => options.UseObservers(first).UseObservers(second));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create("call-1");
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // The seam adds rather than replaces, so a host composes its readings across whatever code
        // configures the container.
        Assert.NotEmpty(first.Seen);
        Assert.NotEmpty(second.Seen);
    }

    // -------------------------------------------------------------------------------------------
    // What it refuses.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task ABadDocument_FailsTheStartAndNamesTheFault()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(BrokenYaml));

        var error = Assert.Single(failure.Errors);
        Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
        Assert.Equal("/policy/stages/0/to/0/stage", error.Pointer);
        Assert.Contains("'nowhere' is not declared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentThatDoesNotParse_FailsTheStart()
    {
        ServiceCollection services = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await services.AddAgentCoreAsync(options =>
            {
                options.ConfigurationPath = "no-such-extension.txt";
                options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello")));
            }, TestContext.Current.CancellationToken));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
    }

    [Fact]
    public async Task NoDocumentAtAll_FailsTheStartAndSaysWhatToSet()
    {
        ServiceCollection services = new();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await services.AddAgentCoreAsync(
                options => options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello"))),
                TestContext.Current.CancellationToken));

        Assert.Contains("names no document", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoDocuments_FailTheStart()
    {
        ServiceCollection services = new();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await services.AddAgentCoreAsync(options =>
            {
                options.Configuration = ConfigurationLoader.LoadYaml(OneAgentYaml);
                options.ConfigurationPath = "config/example.yaml";
                options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello")));
            }, TestContext.Current.CancellationToken));

        Assert.Contains("names two documents", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoChatClientAdapter_FailsTheStart()
    {
        ServiceCollection services = new();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await services.AddAgentCoreAsync(
                options => options.Configuration = ConfigurationLoader.LoadYaml(OneAgentYaml),
                TestContext.Current.CancellationToken));

        Assert.Contains("UseChatClients", failure.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    /// <summary>Composes the guarded graph over one offline model for each node.</summary>
    /// <returns>The provider a test resolves from.</returns>
    private static Task<ServiceProvider> BuildGuardedGraphAsync()
    {
        RoutingChatClientFactory models = new(new SequencedChatClient("ROUTED"));
        models.Route("human", new SequencedChatClient("ESCALATED"));
        models.Route("bot", new SequencedChatClient("HANDLED"));

        return BuildAsync(GuardedGraphYaml, options => options.UseChatClients(_ => models));
    }

    /// <summary>Calls one declared tool, so a test reads which port the built-in holds.</summary>
    /// <param name="provider">The composed container.</param>
    /// <param name="toolId">The tool id the document declares.</param>
    /// <param name="argument">The one argument name the built-in fills.</param>
    /// <param name="value">The value that argument carries.</param>
    private static async Task CallToolAsync(ServiceProvider provider, string toolId, string argument, string value)
    {
        var declaration = provider
            .GetRequiredService<AgentCoreConfiguration>()
            .Tools
            .Single(tool => string.Equals(tool.Id, toolId, StringComparison.Ordinal));

        var function = Assert.IsAssignableFrom<AIFunction>(
            provider.GetRequiredService<IAgentToolFactory>().Create(declaration));

        await function.InvokeAsync(
            new AIFunctionArguments { [argument] = value },
            TestContext.Current.CancellationToken);
    }

    private static async Task<ServiceProvider> BuildAsync(string yaml, Action<AgentCoreOptions>? configure = null)
    {
        ServiceCollection services = new();
        await ConfigureAsync(services, yaml, configure);
        return services.BuildServiceProvider();
    }

    private static async Task ConfigureAsync(ServiceCollection services, string yaml, Action<AgentCoreOptions>? configure)
        => await services.AddAgentCoreAsync(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(yaml);
            options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello")));
            configure?.Invoke(options);
        });

    /// <summary>An observer a host binds, which keeps every fact it was offered, in order.</summary>
    private sealed class RecordingCallObserver : ICallObserver
    {
        private readonly Lock _gate = new();
        private readonly List<CallEventKind> _seen = [];

        /// <summary>Gets what this observer read, in the order the call produced it.</summary>
        public IReadOnlyList<CallEventKind> Seen
        {
            get
            {
                // A delivery may land on a thread of its own, so the reading is taken under the gate.
                lock (_gate)
                {
                    return [.. _seen];
                }
            }
        }

        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _seen.Add(callEvent.Kind);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An observer that refuses every fact, so the isolation of the seam is observable.</summary>
    private sealed class ThrowingCallObserver : ICallObserver
    {
        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the host's observer is broken");
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

    /// <summary>An audit vendor a test registers, which opens one store the test already holds.</summary>
    private sealed class TestAuditSinkAdapter(RecordingAuditSink store) : IAuditSinkAdapter
    {
        public string Kind => "test";

        public ValueTask<IAuditSinkPort> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IAuditSinkPort>(store);
    }

    /// <summary>An audit store that keeps what it accepted, so what the document opened is observable.</summary>
    /// <remarks>
    /// It is not <see cref="InMemoryAuditSink"/>, on purpose: the test has to tell the vendor the
    /// document named apart from the built-in the document would have fallen back to.
    /// </remarks>
    private sealed class RecordingAuditSink : IAuditSinkPort
    {
        private readonly Lock _gate = new();
        private readonly List<AuditEvent> _events = [];

        /// <summary>Gets the events this store accepted, in the order they arrived.</summary>
        public IReadOnlyList<AuditEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _events.Add(auditEvent);
            }

            return ValueTask.CompletedTask;
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

    /// <summary>A store a host registers in place of the default one.</summary>
    private sealed class CountingCallSessionStore : ICallSessionStore
    {
        public ValueTask<CallSession?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CallSession?>(null);

        public ValueTask AddAsync(CallSession session, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}

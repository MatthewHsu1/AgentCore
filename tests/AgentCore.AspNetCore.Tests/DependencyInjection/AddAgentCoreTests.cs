using AgentCore.TestSupport;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.Application.Transcript.Memory;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Tools;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Xunit;
using AgentCore.AspNetCore.DependencyInjection.Startup;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// The composition root. It loads, validates, resolves, compiles, and registers, in that order.
/// </summary>
/// <remarks>
/// A configuration defect stops the host at start and never on the first call. Every test proves
/// that by starting a host and nothing else, with no request anywhere.
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

    // The same agent, and a document that names a telemetry vendor.
    private const string TelemetryYaml =
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
          telemetry: { kind: test }
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

    // A state slot's from: names a tool no tools: entry declares, and no mcp: server offers it
    // either, so nothing in the served set ever resolves it.
    private const string UndeclaredToolYaml =
        """
        apiVersion: agentcore/v1
        name: broken-tool-reference
        state:
          orderStatus: { type: string, writer: tool, from: lookup_order.status }
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

    // An agent's tools: names an id nothing serves. Unlike UndeclaredToolYaml, the fault sits in
    // agents.items[].tools rather than state:, so the pointer must name the agent and not a bare
    // /tools.
    private const string UndeclaredAgentToolYaml =
        """
        apiVersion: agentcore/v1
        name: broken-agent-tool-reference
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ no_such_tool ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // Declares no tools: at all. 'discovered_only' is served only by a fake IToolSource the test
    // registers, never named anywhere in the document itself, so the only way this boots is if the
    // reference pass resolves against what got discovered rather than what got declared.
    private const string DiscoveredOnlyToolYaml =
        """
        apiVersion: agentcore/v1
        name: discovered-only-tool
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ discovered_only ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // A kind: agent tool reaches no source at all: the compiler builds it once the agent it names
    // has compiled, so the registry never holds it. The reference pass must still let front's
    // tools: [ ask_specialist ] through, or every delegating document fails to boot.
    // A declared kind: agent tool whose id a registered source also discovers. The collision is only
    // found after every source has answered, so by then the source is open.
    private const string CollidingAgentToolYaml =
        """
        apiVersion: agentcore/v1
        name: colliding
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
        policy:
          initial: talk
          stages:
            - { id: talk, agent: front, terminal: true }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    private const string DelegatingAgentToolYaml =
        """
        apiVersion: agentcore/v1
        name: delegating
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
        policy:
          initial: talk
          stages:
            - { id: talk, agent: front, terminal: true }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // BrokenYaml's structural defect (an unreachable policy transition), plus an mcp: server whose
    // command names a binary that does not exist. Decision 15's whole point is that the structural
    // error below must surface without AgentCore ever trying to reach that server: a missing
    // executable fails Process.Start synchronously, so if discovery ran first this would instead
    // report the MCP failure. See AddAgentCore_TheStructuralFaultSurfaces_BeforeMcpIsEverAsked.
    private const string StructuralFaultPlusUnreachableMcpYaml =
        """
        apiVersion: agentcore/v1
        name: broken-plus-mcp
        mcp:
          - id: bogus-server
            transport: stdio
            command: ["/definitely-not-a-real-binary-agentcore-task5-test"]
            allow: ["*"]
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
    public async Task AddAgentCore_RegistersTheInMemorySessionsByDefault()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var sessions = provider.GetRequiredService<ICallSessions>();

        Assert.IsType<InMemoryCallSessions>(sessions);
        Assert.Same(sessions, provider.GetRequiredService<ICallSessions>());
    }

    [Fact]
    public async Task AddAgentCore_RunsTheIdleSweepForTheDefaultSessions()
    {
        // Expiry needs something to drive it. Without this the idle timeout never fires and the
        // text path holds every call a caller walked away from for the life of the process.
        using var provider = await BuildAsync(OneAgentYaml);

        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is CallSessionSweeper);
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

        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<InvalidOperationException>(
            provider.GetRequiredService<CompiledAgent>);

        Assert.Contains("has not booted", failure.Message, StringComparison.Ordinal);
        Assert.Contains("StartAsync", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAgentCore_KeepsSessionsTheHostRegisteredFirst()
    {
        CountingCallSessions mine = new();
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
        builder.Services.AddSingleton<ICallSessions>(mine);
        ConfigureServices(builder.Services, OneAgentYaml, null);

        using var provider = await StartAsync(builder.Build());

        // A distributed store replaces the default one, and the default steps aside.
        Assert.Same(mine, provider.GetRequiredService<ICallSessions>());
    }

    // -------------------------------------------------------------------------------------------
    // Telemetry shuts down with the host. The container owns the session, so its disposal is the
    // flush — which is the one path a start that failed also reaches.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AddAgentCore_RegistersTheTelemetrySessionTheDocumentNames()
    {
        var (host, adapter) = await BuildTelemetryHostAsync();

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
        using var provider = await BuildAsync(OneAgentYaml);

        Assert.Null(provider.GetService<ITelemetrySession>());
    }

    [Fact]
    public async Task AddAgentCore_FlushesTheTelemetrySessionWhenTheHostShutsDown()
    {
        var (host, adapter) = await BuildTelemetryHostAsync();

        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, adapter.Session.Flushes);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(1, adapter.Session.Flushes);
    }

    [Fact]
    public async Task AddAgentCore_FlushesTheTelemetrySessionOnceWhenTheHostIsDisposedTwice()
    {
        var (host, adapter) = await BuildTelemetryHostAsync();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        // An adapter's session is not required to survive being drained twice, so the second call
        // has to be a no-op.
        host.Dispose();
        host.Dispose();

        Assert.Equal(1, adapter.Session.Flushes);
    }

    // -------------------------------------------------------------------------------------------
    // The audit chain shuts down with the host. An event is ACCEPTED when AppendAsync returns, so a
    // stop that does not drain the queue loses every row still in it.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AddAgentCore_DrainsTheAuditQueueWhenTheHostShutsDown()
    {
        var (host, store) = await BuildAuditHostAsync();

        await host.StartAsync(TestContext.Current.CancellationToken);

        await host.Services
            .GetRequiredService<IAuditSinkPort>()
            .AppendAsync(AuditRow(1), TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(1, store.Written);
    }

    [Fact]
    public async Task AddAgentCore_ClosesTheAuditStoreOnlyAfterTheQueueHasDrained()
    {
        var (host, store) = await BuildAuditHostAsync();

        await host.StartAsync(TestContext.Current.CancellationToken);

        await host.Services
            .GetRequiredService<IAuditSinkPort>()
            .AppendAsync(AuditRow(1), TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);

        // The container closes what it resolved before it closes the boot that still owns the store
        // behind the queue. Closing that store first would hand the drain a store which can no
        // longer accept the rows it already promised to keep.
        host.Dispose();

        Assert.True(store.Closed);
        Assert.Equal(1, store.WrittenWhenClosed);
    }

    [Fact]
    public async Task AddAgentCore_ClosesTheTranscriptStoreWhenTheHostShutsDown()
    {
        RecordingTranscriptStore store = new();
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
        ConfigureServices(
            builder.Services,
            VendorTranscriptYaml,
            options => options.UseTranscriptStores(new TestTranscriptStoreAdapter(store)));

        IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.True(store.Closed);
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

    // -------------------------------------------------------------------------------------------
    // The document picks the vendor, and no code names one: the point of the adapter seam.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheAdapterOverload_LetsTheDocumentPickTheVendorByItsKind()
    {
        // 'kind: openai' selects the adapter registered under that kind. The host lists what it
        // supports, once, and the document decides which entry runs.
        using var provider = await BuildAsync(OneAgentYaml, options => options.UseChatClients(
            new FakeChatClientAdapter("openai", () => new FragmentingChatClient("routed")),
            new FakeChatClientAdapter("anthropic", () => new FragmentingChatClient("wrong vendor"))));

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
                new FakeChatClientAdapter("openai", () => new FragmentingChatClient("hello")))));

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
                return new RoutingChatClientFactory(new FragmentingChatClient("awaited"));
            }));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create();
        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal("awaited", turn.ReplyText);
    }

    [Fact]
    public async Task AToolSource_SeesTheChatClientFactoryAlreadyBuilt()
    {
        // The seam that builds the factory only runs once, so a null capture here means the tools
        // were built before it ran — exactly the ordering ui.draw depends on.
        IChatClientFactory? builtFactory = null;
        IChatClientFactory? seenWhenToolsWereBuilt = null;

        using var provider = await BuildAsync(OneAgentYaml, options =>
        {
            options.UseChatClients((_, _) =>
            {
                builtFactory = new RoutingChatClientFactory(new FragmentingChatClient("hello"));
                return ValueTask.FromResult(builtFactory);
            });

            options.AddToolSource(_ => new SpyToolSource(() => seenWhenToolsWereBuilt = builtFactory));
        });

        Assert.NotNull(provider.GetRequiredService<CompiledAgent>());
        Assert.NotNull(seenWhenToolsWereBuilt);
        Assert.Same(builtFactory, seenWhenToolsWereBuilt);
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
                options.AddToolSource(startup => new HttpToolSource(client, startup.Secrets));
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
            options => options.AddToolSource(startup => new HttpToolSource(client, startup.Secrets))));

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
            _ => new RoutingChatClientFactory(new FragmentingChatClient(string.Empty))));
        var session = provider.GetRequiredService<ICallSessionFactory>().Create();

        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal("One moment please. I will try that again.", turn.ReplyText);
        Assert.NotEqual(CallSession.FallbackReply, turn.ReplyText);
    }

    [Fact]
    public async Task AQuietTurn_SpeaksTheDefaultFallbackWhenTheDocumentNamesNone()
    {
        using var provider = await BuildAsync(OneAgentYaml, options => options.UseChatClients(
            _ => new RoutingChatClientFactory(new FragmentingChatClient(string.Empty))));
        var session = provider.GetRequiredService<ICallSessionFactory>().Create();

        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal(CallSession.FallbackReply, turn.ReplyText);
    }

    [Fact]
    public async Task AddAgentCore_KeepsAnEvaluationServiceTheHostRegisteredFirst()
    {
        EvaluationSampler mine = new(rate: 1);
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
        builder.Services.AddSingleton(mine);
        ConfigureServices(builder.Services, OneAgentYaml, null);

        using var provider = await StartAsync(builder.Build());

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
        Assert.All(events, AuditEventVocabulary.Validate);
    }

    [Fact]
    public async Task ADocumentThatNamesNoAuditProvider_StillOpensTheMemorySink()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        // The turn loop produces the events of D23 whatever a document says, so the seam that receives
        // them has a working default rather than a null. That is what lets every reading of a call be
        // unconditional, and what lets a first run and a test work with no database.
        Assert.NotNull(provider.GetService<IAuditSinkPort>());
        Assert.IsType<InMemoryAuditSink>(provider.GetRequiredService<QueuedAuditSink>().Store);
    }

    [Fact]
    public async Task ADocumentThatNamesTheMemoryKind_OpensTheSameSinkAsNamingNothing()
    {
        using var provider = await BuildAsync(MemoryAuditYaml);

        // memory is this library's own name and it needs no registered vendor, so writing it says out
        // loud what leaving the block out does quietly. The startup warning is the difference.
        Assert.IsType<InMemoryAuditSink>(provider.GetRequiredService<QueuedAuditSink>().Store);
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
        Assert.Same(store, provider.GetRequiredService<QueuedAuditSink>().Store);
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
        => Assert.IsType<InMemoryAuditSink>(provider.GetRequiredService<QueuedAuditSink>().Store);

    // -------------------------------------------------------------------------------------------
    // The transcript store: named by providers.transcript, and never absent.
    // -------------------------------------------------------------------------------------------

    // The same agent, served by a transcript vendor the host registers itself.
    private const string VendorTranscriptYaml =
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
          transcript: { kind: test }
        """;

    // The transcript store opens at step 4b and the moderation vendor is built at step 4c, so a
    // document that names both puts a failure strictly after an open. Nothing else in the boot has
    // that shape.
    private const string TranscriptThenModerationFailureYaml =
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
          transcript: { kind: test }
          moderation: { kind: test }
        """;

    [Fact]
    public async Task ADocumentThatNamesNoTranscriptProvider_StillOpensTheMemoryStore()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        // The turn loop writes the words of every call whatever a document says, so this seam has a
        // working default rather than a null, and a first run needs no database.
        Assert.NotNull(provider.GetService<ITranscriptStore>());
        Assert.IsType<InMemoryTranscriptStore>(provider.GetRequiredService<ITranscriptStore>());
    }

    [Fact]
    public async Task ATranscriptVendorTheDocumentNames_IsTheStoreTheTurnWritesTo()
    {
        RecordingTranscriptStore store = new();
        using var provider = await BuildAsync(
            VendorTranscriptYaml,
            options => options.UseTranscriptStores(new TestTranscriptStoreAdapter(store)));

        var session = provider.GetRequiredService<ICallSessionFactory>().Create("call-1");
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        // The host lists its vendors once and providers.transcript.kind picks one. Nothing but the
        // document decides where the words of a call land.
        Assert.Same(store, provider.GetRequiredService<ITranscriptStore>());
        Assert.Equal(["user", "assistant"], store.Roles);
    }

    [Fact]
    public async Task ATranscriptKindThisHostDoesNotRegister_FailsTheStart()
    {
        // A document that asked for something this host cannot give fails while the host starts, and
        // never on a call.
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(VendorTranscriptYaml));

        Assert.Contains("test", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A transcript vendor that hands over the store the test holds.</summary>
    private sealed class TestTranscriptStoreAdapter(RecordingTranscriptStore store) : ITranscriptStoreAdapter
    {
        public string Kind => "test";

        public ValueTask<ITranscriptStore> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITranscriptStore>(store);
    }

    /// <summary>A store 1 backing that keeps the role of every row it accepted.</summary>
    private sealed class RecordingTranscriptStore : ITranscriptStore, IAsyncDisposable
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

        public ValueTask AppendAsync(
            IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _roles.AddRange(messages.Select(message => message.Content.Role.Value));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RewriteAsync(
            string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }
    }

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
        Assert.All(events, AuditEventVocabulary.Validate);
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

    /// <summary>
    /// The reference pass runs after discovery, against what the tool registry actually serves. A
    /// state slot's <c>from:</c> naming a tool nothing serves — not declared, and no <c>mcp:</c>
    /// server offers it either — still stops the boot rather than leaving the slot silently unfilled.
    /// </summary>
    [Fact]
    public async Task AnUndeclaredToolInAStateSlot_FailsTheStartAndNamesTheTool()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(UndeclaredToolYaml));

        var error = Assert.Single(failure.Errors);
        Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
        Assert.Equal("/state/orderStatus/from", error.Pointer);
        Assert.Contains("lookup_order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentToolReferencingAnIdNothingServes_FailsTheStartNamingTheAgent()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(UndeclaredAgentToolYaml));

        var error = Assert.Single(failure.Errors);
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
        var source = new DisposeTrackingToolSource();

        await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
            UndeclaredAgentToolYaml,
            options => options.AddToolSource(_ => source)));

        Assert.True(source.Disposed);
    }

    /// <summary>
    /// The transcript store is opened at step 4b, and the moderation vendor is built after it. A
    /// document that names a moderation kind this host does not register therefore fails with the
    /// store already open, and nothing between the two steps has taken ownership of it.
    /// </summary>
    [Fact]
    public async Task AFailureAfterTheTranscriptStoreOpens_StillClosesTheStore()
    {
        RecordingTranscriptStore store = new();

        await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
            TranscriptThenModerationFailureYaml,
            options => options
                .UseTranscriptStores(new TestTranscriptStoreAdapter(store))
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

        await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
            CollidingAgentToolYaml,
            options => options.AddToolSource(_ => source)));

        Assert.True(source.Disposed);
    }

    /// <summary>
    /// An id no <c>tools:</c> entry names, served only by a discovering source, still satisfies an
    /// agent's reference through <see cref="AgentCoreServiceCollectionExtensions.AddAgentCore"/>
    /// end to end, public API only. An <c>mcp:</c> server's tools work exactly this way: decision 15
    /// requires the reference pass to resolve against what got discovered, not just what got declared.
    /// </summary>
    [Fact]
    public async Task ADiscoveredOnlyTool_SatisfiesAnAgentsReferenceThroughTheRealBoot()
    {
        using var provider = await BuildAsync(
            DiscoveredOnlyToolYaml,
            options => options.AddToolSource(_ => new DiscoveredOnlyToolSource("discovered_only")));

        Assert.NotNull(provider.GetRequiredService<CompiledAgent>());
        Assert.True(provider.GetRequiredService<ToolRegistry>().Contains("discovered_only"));
    }

    /// <summary>
    /// <see cref="ToolRegistryBuilder.VerifyEveryDeclarationIsServed"/> carves <see cref="ToolKind.Agent"/>
    /// out of its own "every declaration is served" rule, because that kind reaches no source — the
    /// compile table builds it once the agent it names has compiled. The reference pass in the
    /// composition root has to carve the same kind out of its own served-ids set for the same reason,
    /// or a document exactly like this one — the shape section 8.1 calls agent-as-tool — fails to
    /// boot even though it declares nothing wrong.
    /// </summary>
    [Fact]
    public async Task ADelegatingAgentTool_BootsBecauseKindAgentReachesNoSource()
    {
        using var provider = await BuildAsync(DelegatingAgentToolYaml);

        Assert.NotNull(provider.GetRequiredService<CompiledAgent>());
    }

    /// <summary>
    /// Decision 15's whole justification: a YAML typo must never cost a round trip to an MCP server.
    /// This document carries both a structural defect and an <c>mcp:</c> server whose command does
    /// not exist, so the two possible orderings are observably different: structure-first reports
    /// the policy fault and never touches the server; discovery-first would instead report that the
    /// server could not be reached, because <c>Process.Start</c> on a missing executable fails
    /// synchronously, well before any structural error would ever be found. The error alone only
    /// infers the order; <see cref="SpyToolSource"/> observes it directly by recording whether
    /// <c>ProvideAsync</c> was ever called on any source at all — structure-first means
    /// <see cref="AgentCore.Application.Tools.ToolRegistryBuilder.BuildAsync"/> never runs, so nothing
    /// is ever asked, not even a source that serves nothing.
    /// </summary>
    [Fact]
    public async Task AddAgentCore_TheStructuralFaultSurfaces_BeforeMcpIsEverAsked()
    {
        var asked = false;

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => BuildAsync(
            StructuralFaultPlusUnreachableMcpYaml,
            options =>
            {
                // Registered before McpToolSource, so this is asked first if discovery runs at all —
                // a true observer of whether ToolRegistryBuilder.BuildAsync began, not just of
                // whether the MCP source in particular got asked.
                options.AddToolSource(_ => new SpyToolSource(() => asked = true));
                options.AddToolSource(_ => new McpToolSource());
            }));

        var error = Assert.Single(failure.Errors);
        Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
        Assert.Equal("/policy/stages/0/to/0/stage", error.Pointer);
        Assert.Contains("'nowhere' is not declared", error.Message, StringComparison.Ordinal);

        // Distinguishes the orders directly: an MCP connection failure would name the server id.
        Assert.DoesNotContain("bogus-server", failure.Message, StringComparison.Ordinal);

        Assert.False(asked);
    }

    [Fact]
    public async Task ADocumentThatDoesNotParse_FailsTheStart()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => StartBareAsync(options =>
            {
                options.ConfigurationPath = "no-such-extension.txt";
                options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
            }));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
    }

    [Fact]
    public async Task NoDocumentAtAll_FailsTheStartAndSaysWhatToSet()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartBareAsync(
                options => options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")))));

        Assert.Contains("names no document", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoDocuments_FailTheStart()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartBareAsync(options =>
            {
                options.Configuration = ConfigurationLoader.LoadYaml(OneAgentYaml);
                options.ConfigurationPath = "config/example.yaml";
                options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
            }));

        Assert.Contains("names two documents", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoChatClientAdapter_FailsTheStart()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartBareAsync(options => options.Configuration = ConfigurationLoader.LoadYaml(OneAgentYaml)));

        Assert.Contains("UseChatClients", failure.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    /// <summary>Composes the guarded graph over one offline model for each node.</summary>
    /// <returns>The provider a test resolves from.</returns>
    private static Task<StartedHost> BuildGuardedGraphAsync()
    {
        RoutingChatClientFactory models = new(new FragmentingChatClient("ROUTED"));
        models.Route("human", new FragmentingChatClient("ESCALATED"));
        models.Route("bot", new FragmentingChatClient("HANDLED"));

        return BuildAsync(GuardedGraphYaml, options => options.UseChatClients(_ => models));
    }

    /// <summary>Calls one declared tool, so a test reads which port the built-in holds.</summary>
    /// <param name="provider">The composed container.</param>
    /// <param name="toolId">The tool id the document declares.</param>
    /// <param name="argument">The one argument name the built-in fills.</param>
    /// <param name="value">The value that argument carries.</param>
    private static async Task CallToolAsync(StartedHost provider, string toolId, string argument, string value)
    {
        var declaration = provider
            .GetRequiredService<AgentCoreConfiguration>()
            .Tools
            .Single(tool => string.Equals(tool.Id, toolId, StringComparison.Ordinal));

        var function = Assert.IsAssignableFrom<AIFunction>(
            provider.GetRequiredService<ToolRegistry>().Resolve(declaration.Id));

        await function.InvokeAsync(
            new AIFunctionArguments { [argument] = value },
            TestContext.Current.CancellationToken);
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

    private static async Task<(IHost Host, ClosingAuditSink Store)> BuildAuditHostAsync()
    {
        ClosingAuditSink store = new();
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
        ConfigureServices(
            builder.Services,
            VendorAuditYaml,
            options => options.UseAuditSinks(new TestAuditSinkAdapter(store)));

        return (builder.Build(), store);
    }

    /// <summary>One well-formed event, which is all a drain has to carry.</summary>
    /// <param name="sequence">The chain position.</param>
    /// <returns>The event.</returns>
    private static AuditEvent AuditRow(long sequence) => new()
    {
        CallId = "call-1",
        Sequence = sequence,
        Kind = AuditEventKind.TurnCompleted,
        OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
    };

    /// <summary>Starts a host on one document, which is where the whole boot happens.</summary>
    /// <param name="yaml">The document to boot.</param>
    /// <param name="configure">The host's own word on the options.</param>
    /// <returns>The started host, read as the container it is.</returns>
    private static async Task<StartedHost> BuildAsync(string yaml, Action<AgentCoreOptions>? configure = null)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
        ConfigureServices(builder.Services, yaml, configure);

        return await StartAsync(builder.Build());
    }

    /// <summary>Starts one host, and closes it itself when the start fails.</summary>
    /// <param name="host">The host to start.</param>
    /// <returns>The started host.</returns>
    /// <remarks>
    /// A failed start never stops what already started, so disposal is the only cleanup path — and
    /// it is the one a real host takes too, inside <c>RunAsync</c>'s own finally. StopAsync is
    /// deliberately not called here: on net10 a host that failed to start throws
    /// <see cref="ArgumentNullException"/> out of StopAsync when the failure was a constructor.
    /// </remarks>
    private static async Task<StartedHost> StartAsync(IHost host)
    {
        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return new StartedHost(host);
    }

    /// <summary>Starts a host on nothing but the options a test writes, with no document default.</summary>
    /// <param name="configure">The only word on the options.</param>
    /// <returns>The started host, for the tests that expect it never to get one.</returns>
    private static async Task<StartedHost> StartBareAsync(Action<AgentCoreOptions> configure)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
        builder.Services.AddAgentCore(configure);

        return await StartAsync(builder.Build());
    }

    private static void ConfigureServices(
        IServiceCollection services, string yaml, Action<AgentCoreOptions>? configure)
        => services.AddAgentCore(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(yaml);
            options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
            configure?.Invoke(options);
        });

    /// <summary>An adapter that starts nothing and hands back a session that records its flush.</summary>
    private sealed class FlushRecordingTelemetryAdapter(string kind) : ITelemetryAdapter
    {
        public string Kind => kind;

        public FlushRecordingSession Session { get; } = new();

        public ValueTask<ITelemetrySession> StartAsync(
            TelemetryProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITelemetrySession>(Session);
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
    private sealed class TestAuditSinkAdapter(IAuditSinkPort store) : IAuditSinkAdapter
    {
        public string Kind => "test";

        public ValueTask<IAuditSinkPort> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(store);
    }

    /// <summary>An audit store that is slow to write and records what it held when it was closed.</summary>
    /// <remarks>
    /// The delay is the whole test: a shutdown that does not wait for the queue returns long before
    /// this store has been handed anything, so a lost row is a failed assertion rather than a race.
    /// </remarks>
    private sealed class ClosingAuditSink : IAuditSinkPort, IAsyncDisposable
    {
        private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(200);

        private int _written;

        /// <summary>Gets the number of events this store has written.</summary>
        public int Written => Volatile.Read(ref _written);

        /// <summary>Gets whether this store was closed.</summary>
        public bool Closed { get; private set; }

        /// <summary>Gets how many events this store had written by the time it was closed.</summary>
        public int WrittenWhenClosed { get; private set; }

        public async ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            await Task.Delay(WriteDelay, CancellationToken.None);
            Interlocked.Increment(ref _written);
        }

        public ValueTask DisposeAsync()
        {
            Closed = true;
            WrittenWhenClosed = Written;
            return ValueTask.CompletedTask;
        }
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

    /// <summary>Sessions a host registers in place of the default ones.</summary>
    private sealed class CountingCallSessions : ICallSessions
    {
        public ValueTask<CallSession> OpenAsync(string? callId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<CallSession?> TryGetAsync(string callId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CallSession?>(null);

        public ValueTask CloseAsync(string callId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
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
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                [new ToolRegistration(id, "A tool discovered but never declared.", () => AIFunctionFactory.Create(() => "ok", id))]);
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

    /// <summary>A started host, read as the container it is.</summary>
    /// <param name="host">The host to read services from, and to close on the way out.</param>
    /// <remarks>
    /// Disposing this disposes the host, which disposes the container. That is the whole shutdown
    /// path: nothing here calls StopAsync, because a host that failed to start never gets one.
    /// </remarks>
    private sealed class StartedHost(IHost host) : IServiceProvider, IDisposable
    {
        public object? GetService(Type serviceType) => host.Services.GetService(serviceType);

        public void Dispose() => host.Dispose();
    }

    /// <summary>A tool source a host registers, which records whether it was ever closed.</summary>
    /// <param name="servedId">One id to serve, or <see langword="null"/> to serve nothing.</param>
    private sealed class DisposeTrackingToolSource(string? servedId = null) : IToolSource, IAsyncDisposable
    {
        /// <summary>Gets whether this source was disposed.</summary>
        public bool Disposed { get; private set; }

        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                servedId is null
                    ? []
                    : [new ToolRegistration(servedId, "A tool discovered under a claimed id.", () => AIFunctionFactory.Create(() => "ok", servedId))]);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

using System.Text.Json.Nodes;
using AgentCore.Application.Calls;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The one wiring a host can actually write: set a knowledge scope on the session, run a turn,
/// and have the scope still be there when the retrieval delegate reads it.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of the scope sets it and reads it back without a turn loop in between, and
/// every one of them passed while the feature was wired shut: the loop built its turn without the
/// session's scope, so the store never saw it. The defect lives in the interaction, so the test
/// has to run the interaction.
/// </para>
/// <para>
/// The agent here is <c>scoped: true</c>, which makes the erasure visible twice over: the provider's
/// own gate refuses to call the store at all with no scope set, so a broken carry-through shows up
/// as a store nobody searched as well as a scope nobody could read.
/// </para>
/// </remarks>
public sealed class KnowledgeScopeThroughCallSessionTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: knowledge-scope-through-callsession
        agents:
          items:
            - id: only
              instructions: "answer the caller"
              knowledge: { mode: prefetch, scoped: true }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: only }
        """;

    /// <summary>
    /// The document the three <c>Scope_</c> facts run against. Unlike <see cref="Yaml"/>, it declares
    /// <c>providers.knowledge.scope</c>, so <c>StateKnowledgeScope.Compose</c> builds a scope from
    /// state rather than passing the host's ambient through unchanged — which is why the two
    /// <c>RunTurnAsync_UnderAHostScope_</c> facts above must keep using the plain <see cref="Yaml"/>:
    /// their <c>Assert.Same</c> needs the ambient untouched.
    /// </summary>
    private const string ScopedYaml =
        """
        apiVersion: agentcore/v1
        name: knowledge-scope-through-callsession-scoped
        state:
          brand: { type: string, writer: extractor, enum: [sole, other] }
          applies_to: { type: string, writer: extractor, enum: [f63, other] }
        extractor:
          model: { ref: fill }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
            scope:
              template: "facets.{key}"
              wildcard:
                value: "*"
                facets: [brand, applies_to]
              fromState: [brand, applies_to]
        agents:
          items:
            - id: only
              instructions: "answer the caller"
              knowledge: { mode: prefetch, scoped: true }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: only }
        """;

    private StubKnowledgePort _port = null!;

    [Fact]
    public async Task RunTurnAsync_UnderAHostScope_TheStoreStillSeesThatScope()
    {
        using SequencedChatClient reply = new("hello there.");
        var port = new StubKnowledgePort([Card("a")]);
        var session = Build(reply, port).Create("call-1");

        var scope = Scope();
        session.Scope = scope;
        await session.RunTurnAsync("the screen says e33", TestContext.Current.CancellationToken);

        Assert.Equal(1, port.Calls);
        Assert.Same(scope, port.ScopeAtTheStore);
    }

    [Fact]
    public async Task RunTurnStreamingAsync_UnderAHostScope_TheStoreStillSeesThatScope()
    {
        // The streaming path enters the ambients a second time, once per step, through
        // ScopedEnumerator. That re-entry drops whatever the first entry dropped, so it needs its own
        // fact rather than an argument from the non-streaming one.
        using SequencedChatClient reply = new("hello there.");
        var port = new StubKnowledgePort([Card("a")]);
        var session = Build(reply, port).Create("call-2");

        var scope = Scope();
        session.Scope = scope;
        await foreach (var update in session
            .RunTurnStreamingAsync("the screen says e33", TestContext.Current.CancellationToken))
        {
            Assert.NotNull(update);
        }

        Assert.Equal(1, port.Calls);
        Assert.Same(scope, port.ScopeAtTheStore);
    }

    [Fact]
    public async Task RunTurnAsync_WithNoHostScope_LeavesTheAmbientAbsent()
    {
        // The counterpart fact. Carrying the scope through must not invent one: an unscoped host
        // still reaches the provider's gate, and the agent is told it cannot look anything up.
        using SequencedChatClient reply = new("hello there.");
        var port = new StubKnowledgePort([Card("a")]);
        var session = Build(reply, port).Create("call-3");

        await session.RunTurnAsync("the screen says e33", TestContext.Current.CancellationToken);

        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task RunTurnAsync_AfterTheTurn_PutsBackWhatTheHostHadOpen()
    {
        // The turn loop opens ambients of its own over the host's. Closing them must restore the
        // host's scope rather than clearing it, or a second turn of the same call runs unscoped.
        using SequencedChatClient reply = new("hello there.");
        var port = new StubKnowledgePort([Card("a")]);
        var session = Build(reply, port).Create("call-4");

        var scope = Scope();
        session.Scope = scope;
        await session.RunTurnAsync("the screen says e33", TestContext.Current.CancellationToken);

        Assert.Same(scope, session.Scope);
    }

    [Fact]
    public async Task Scope_ResumedCall_IsBuiltFromTheRestoredSlots()
    {
        // The host cannot do this itself: Restore runs inside OpenSessionAsync, which is the first
        // statement of the run method, so session.State is empty until the turn has begun.
        using SequencedChatClient reply = new("hello there.");
        var session = ResumedSession(reply, stored: new Dictionary<string, string>
        {
            ["brand"] = "sole",
            ["applies_to"] = "f63",
        });

        await session.RunTurnAsync("is the belt covered?", TestContext.Current.CancellationToken);

        Assert.Equal("sole", _port.ScopeAtTheStore!.Facets["brand"]);
        Assert.Equal("f63", _port.ScopeAtTheStore.Facets["applies_to"]);
    }

    [Fact]
    public async Task Scope_ResumedCall_WithAnOffEnumSlot_LeavesThatSlotWildcard()
    {
        // "F63 Treadmill" is outside applies_to's enum: [f63, other], so Restore's TryWrite refuses
        // it and the slot stays unfilled. Compose then reads it as the wildcard, same as a slot
        // nothing ever wrote — the refused blob value never reaches the search filter.
        using SequencedChatClient reply = new("hello there.");
        var session = ResumedSession(reply, stored: new Dictionary<string, string>
        {
            ["brand"] = "sole",
            ["applies_to"] = "F63 Treadmill",
        });

        await session.RunTurnAsync("is the belt covered?", TestContext.Current.CancellationToken);

        Assert.Equal("sole", _port.ScopeAtTheStore!.Facets["brand"]);
        Assert.Equal("*", _port.ScopeAtTheStore.Facets["applies_to"]);
    }

    [Fact]
    public async Task Scope_NothingKnown_IsAllWildcard()
    {
        using SequencedChatClient reply = new("hello there.");
        var session = FreshSession(reply);

        await session.RunTurnAsync("what are your opening hours?", TestContext.Current.CancellationToken);

        Assert.Equal("*", _port.ScopeAtTheStore!.Facets["brand"]);
        Assert.Equal("*", _port.ScopeAtTheStore.Facets["applies_to"]);
    }

    [Fact]
    public async Task Scope_StreamingTurn_ComposesTheSameScope()
    {
        // The streaming path drives the turn through its own entry point, so it needs its own
        // fact rather than an argument from the single-shot one: the scope the store sees must
        // match what a non-streaming turn composes.
        using SequencedChatClient reply = new("hello there.");
        var session = FreshSession(reply);

        await foreach (var _ in session.RunTurnStreamingAsync("what are your opening hours?", TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal("*", _port.ScopeAtTheStore!.Facets["brand"]);
        Assert.Equal("*", _port.ScopeAtTheStore.Facets["applies_to"]);
    }


    private static CallSessionFactory Build(IChatClient reply, StubKnowledgePort port, string yaml = Yaml)
    {
        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { Knowledge = port });

        return new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
    }

    private CallSession FreshSession(IChatClient reply)
    {
        _port = new StubKnowledgePort([Card("a")]);

        return Build(reply, _port, ScopedYaml).Create("call-fresh");
    }

    private CallSession ResumedSession(IChatClient reply, IReadOnlyDictionary<string, string> stored)
    {
        _port = new StubKnowledgePort([Card("a")]);

        // The real restore path: a CallSessionState of the shape store 0 hands back, fed through
        // CallSessionFactory.Create so Resume/Restore write the slots, rather than the test poking
        // session.State directly and skipping the coercion and enum checks Restore applies.
        CallSessionState state = new()
        {
            Stage = "greeting",
            Slots = stored.ToDictionary(
                pair => pair.Key,
                pair => (JsonNode?)JsonValue.Create(pair.Value),
                StringComparer.Ordinal),
        };

        return Build(reply, _port, ScopedYaml).Create("call-resumed", state);
    }

    private static KnowledgeScope Scope()
        => new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

    private static KnowledgeCard Card(string id)
        => new()
        {
            CardId = id,
            Text = "card " + id,
            Authority = 3,
            SourceRef = "ct900-om",
            SourceLocator = "p.27",
            Score = 0.87,
            ViaLink = false,
        };
}

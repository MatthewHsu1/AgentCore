using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="ClarificationProvider"/>: §7 channel 1's loop, driven directly against a hand-built
/// <see cref="Clarifications"/> and <see cref="StateDocument"/> rather than a real turn. The rows that
/// depend on <see cref="CallSession"/>'s own turn ordering (K35) live in
/// <see cref="CallSessionClarificationTests"/> instead.
/// </summary>
public sealed class ClarificationProviderTests
{
    private const string AppliesToDescription = "The model, as printed on the machine.";

    private const string BrandDescription = "The brand of the caller's machine.";

    private const string AmbiguityYaml =
        """
        apiVersion: agentcore/v1
        name: clarification-wiring
        state:
          applies_to: { type: string, writer: extractor, description: "The model, as printed on the machine.", vocabulary: { from: knowledge } }
        extractor:
          model: { ref: fill }
          when: after_reply
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
              fromState: [applies_to]
              wildcard: { value: "*", facets: [applies_to] }
            ambiguity: { maxCandidates: 6, maxAsks: 2 }
        agents:
          items:
            - id: only
              knowledge: { mode: prefetch, scoped: false }
        """;

    private const string NoAmbiguityYaml =
        """
        apiVersion: agentcore/v1
        name: clarification-wiring-none
        agents:
          items:
            - id: only
        """;

    [Fact]
    public void CompiledAgent_CarriesTheProvider_BeforeTheKnowledgeProvider_WhenAmbiguityIsDeclared()
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(AmbiguityYaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply))
            {
                Knowledge = new StubKnowledgePort([]),
            });

        var agent = ChatClientAgentOf(Assert.Single(compiled.Agents.Values));
        var providers = agent.AIContextProviders ?? [];

        var clarificationIndex = providers.ToList().FindIndex(p => p is ClarificationProvider);
        var knowledgeIndex = providers.ToList().FindIndex(
            p => p.GetType().Name.Contains("TextSearchProvider", StringComparison.Ordinal));

        Assert.True(clarificationIndex >= 0, "the compiled agent must carry a ClarificationProvider.");
        Assert.True(knowledgeIndex >= 0, "the compiled agent must still carry the knowledge provider.");
        Assert.True(clarificationIndex < knowledgeIndex, "the clarification must bind before the knowledge provider.");
    }

    [Fact]
    public void CompiledAgent_CarriesNoProvider_WhenTheDocumentDeclaresNoAmbiguity()
    {
        // K19: an undeclared ambiguity: must not change what a document without it compiles to.
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(NoAmbiguityYaml), new AgentCompilationContext(new FakeChatClientFactory(reply)));

        var agent = ChatClientAgentOf(Assert.Single(compiled.Agents.Values));
        var providers = agent.AIContextProviders ?? [];

        Assert.DoesNotContain(providers, p => p is ClarificationProvider);
    }

    /// <summary>Reads the <see cref="ChatClientAgent"/> under whatever the compiler wrapped it in.</summary>
    private static ChatClientAgent ChatClientAgentOf(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner;
    }

    [Fact]
    public async Task NoTurnIsOpen_EmitsNothing()
    {
        var provider = Provider(maxAsks: 2, maxCandidates: 6, ("applies_to", AppliesToDescription));

        var context = await provider.InvokingAsync(Invoking(new StubSession()), TestContext.Current.CancellationToken);

        Assert.Null(context.Messages);
    }

    [Fact]
    public async Task ARunOnAnotherSession_EmitsNothing()
    {
        // The K29 guard: a delegated kind: agent run's session never matches the caller's own turn.
        StubSession callSession = new();
        StubSession delegatedSession = new();
        var clarifications = new Clarifications();
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);

        var provider = Provider(maxAsks: 2, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var turn = TurnContextScope.Enter(new TurnContext { Session = callSession });
        using var ambients = TurnAmbients.Amend(
            a => a with { State = State(("applies_to", AppliesToDescription)), Clarifications = clarifications });

        var context = await provider.InvokingAsync(
            Invoking(delegatedSession), TestContext.Current.CancellationToken);

        Assert.Null(context.Messages);
        Assert.Equal(0, clarifications.Read("applies_to").NamedAsks);
    }

    [Fact]
    public async Task MaxAsksZero_InjectsNothing_EvenWithAPendingSlot()
    {
        // K38's gate-only mode: the vocabulary and the gate still work, but the channel never speaks.
        StubSession session = new();
        var clarifications = new Clarifications();
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);

        var provider = Provider(maxAsks: 0, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, State(("applies_to", AppliesToDescription)), clarifications);

        var context = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);

        Assert.Null(context.Messages);
        Assert.Equal(0, clarifications.Read("applies_to").NamedAsks);
    }

    [Fact]
    public async Task NegativeMaxAsks_InjectsNothing_RatherThanLettingTheResetAskOnce()
    {
        // K38 again, at a maxAsks the schema does not itself bound. An == 0 gate would fall through
        // to step 2's failing namedAsks < maxAsks test and let step 3's one reset speak.
        StubSession session = new();
        var clarifications = new Clarifications();
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);

        var provider = Provider(maxAsks: -1, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, State(("applies_to", AppliesToDescription)), clarifications);

        var context = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);

        Assert.Null(context.Messages);
        Assert.Equal(0, clarifications.Read("applies_to").NamedAsks);
        Assert.False(clarifications.Read("applies_to").ResetSpent);
    }

    [Fact]
    public async Task ATurnThatNeverCommits_IsNotCharged_AndAsksAgainNextTurn()
    {
        // The instruction is injected before the run. A run that ended in the fallback reply never
        // put the question, so the next turn must ask it rather than treat the slot as answered.
        StubSession session = new();
        var clarifications = new Clarifications();
        var state = State(("applies_to", AppliesToDescription));

        var provider = Provider(maxAsks: 1, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, state, clarifications);

        clarifications.BeginTurn();
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);
        var turn1 = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);
        Assert.NotNull(turn1.Messages);

        // Turn 1 threw or fell back: nothing commits it.
        clarifications.BeginTurn();
        var turn2 = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);

        Assert.NotNull(turn2.Messages);
        Assert.Equal(1, clarifications.Read("applies_to").NamedAsks);
    }

    [Fact]
    public async Task NoPendingList_EmitsNothing()
    {
        StubSession session = new();
        var clarifications = new Clarifications();

        var provider = Provider(maxAsks: 2, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, State(("applies_to", AppliesToDescription)), clarifications);

        var context = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);

        Assert.Null(context.Messages);
    }

    [Fact]
    public async Task AFilledSlot_IsSkipped_EvenWithAStalePendingList()
    {
        // Belt and braces: K30 clears Pending on a successful write, but the loop's own "still
        // unfilled" test must not tell the model a filled slot "is not yet known" regardless.
        StubSession session = new();
        var clarifications = new Clarifications();
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);

        var state = State(("applies_to", AppliesToDescription));
        state.TryWrite("applies_to", JsonSerializer.SerializeToNode("ct900ent"));

        var provider = Provider(maxAsks: 2, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, state, clarifications);

        var context = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);

        Assert.Null(context.Messages);
    }

    [Fact]
    public async Task APendingSlot_SpeaksOnce_AndRecordsWhatWasNamed()
    {
        StubSession session = new();
        var clarifications = new Clarifications();
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);

        var provider = Provider(maxAsks: 2, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, State(("applies_to", AppliesToDescription)), clarifications);

        var context = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);

        var message = Assert.Single(context.Messages!);
        Assert.Equal(ChatRole.System, message.Role);
        Assert.Equal(
            ClarificationText.Instruction(AppliesToDescription, ["ct900", "ct900ent"], 6, first: true),
            message.Text);

        var snapshot = clarifications.Read("applies_to");
        Assert.Equal(1, snapshot.NamedAsks);
        Assert.True(snapshot.AskedThisTurn);
        Assert.Equal(Clarifications.LastNamedKind.Set, snapshot.LastNamed.Kind);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "ct900", "ct900ent" }, snapshot.LastNamed.Values);
    }

    [Fact]
    public async Task TheUnchangedSet_IsNotAskedAgain_UntilItChanges()
    {
        // K37: the caller has already been asked this exact question. maxAsks: 1 makes the reset
        // reachable in a small number of simulated turns.
        StubSession session = new();
        var clarifications = new Clarifications();
        var state = State(("applies_to", AppliesToDescription));

        var provider = Provider(maxAsks: 1, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, state, clarifications);

        // Turn 1: pending set A is new. Asked once (namedAsks -> 1). Each turn here commits, which
        // is what CallSession does once the turn's own words reach the caller.
        clarifications.BeginTurn();
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);
        var turn1 = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);
        Assert.NotNull(turn1.Messages);
        Assert.Equal(1, clarifications.Read("applies_to").NamedAsks);
        clarifications.CommitAsks();

        // Turn 2: the same set. namedAsks is at the cap (1), but K37 suppresses before the cap is
        // even reached, because nothing changed.
        clarifications.BeginTurn();
        var turn2 = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);
        Assert.Null(turn2.Messages);
        Assert.Equal(1, clarifications.Read("applies_to").NamedAsks);
        Assert.False(clarifications.Read("applies_to").ResetSpent);
        clarifications.CommitAsks();

        // Turn 3: the set changes. namedAsks is at the cap and differs from lastNamed, so the one
        // reset fires: namedAsks returns to 1 (not 0), and the reset is spent.
        clarifications.BeginTurn();
        clarifications.Update("applies_to", s => s.Pending = ["ct800", "ct800ent"]);
        var turn3 = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);
        Assert.NotNull(turn3.Messages);
        clarifications.CommitAsks();
        var afterReset = clarifications.Read("applies_to");
        Assert.Equal(1, afterReset.NamedAsks);
        Assert.True(afterReset.ResetSpent);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "ct800", "ct800ent" }, afterReset.LastNamed.Values);

        // Turn 4: the set changes again. The reset is already spent, so nothing is asked — the
        // call's total stays at exactly 2 x maxAsks = 2, matching turn 1 and turn 3.
        clarifications.BeginTurn();
        clarifications.Update("applies_to", s => s.Pending = ["xt285", "xt385"]);
        var turn4 = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);
        Assert.Null(turn4.Messages);
        Assert.Equal(1, clarifications.Read("applies_to").NamedAsks);
    }

    [Fact]
    public async Task TwoDifferentOverCapSets_AreNotBothSpoken()
    {
        // K37: above maxCandidates neither channel names a list, so two different over-cap sets read
        // as the identical question and the sentinel record suppresses the second.
        StubSession session = new();
        var clarifications = new Clarifications();
        var state = State(("applies_to", AppliesToDescription));

        var provider = Provider(maxAsks: 2, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, state, clarifications);

        clarifications.BeginTurn();
        clarifications.Update("applies_to", s => s.Pending = ["a", "b", "c", "d", "e", "f", "g"]);
        var turn1 = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);
        Assert.NotNull(turn1.Messages);
        Assert.Equal(1, clarifications.Read("applies_to").NamedAsks);
        clarifications.CommitAsks();

        clarifications.BeginTurn();
        clarifications.Update("applies_to", s => s.Pending = ["h", "i", "j", "k", "l", "m", "n"]);
        var turn2 = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);

        Assert.Null(turn2.Messages);
        Assert.Equal(1, clarifications.Read("applies_to").NamedAsks);
    }

    [Fact]
    public async Task TwoPendingSlots_SpeakTwoMessages_AndTheSecondOpensWithAnotherThing()
    {
        StubSession session = new();
        var clarifications = new Clarifications();
        clarifications.Update("brand", s => s.Pending = ["sole", "spirit"]);
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);

        var provider = Provider(
            maxAsks: 2,
            maxCandidates: 6,
            ("brand", BrandDescription),
            ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(
            session, State(("brand", BrandDescription), ("applies_to", AppliesToDescription)), clarifications);

        var context = await provider.InvokingAsync(Invoking(session), TestContext.Current.CancellationToken);

        Assert.NotNull(context.Messages);
        var messages = context.Messages!.ToList();
        Assert.Equal(2, messages.Count);
        Assert.StartsWith("One thing is not yet known: ", messages[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("Another thing is not yet known: ", messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInstructionNeverRemovesACard()
    {
        // Chained the way MAF actually runs two providers on one agent: each InvokingAsync merges
        // onto the context the one before it returned, so this proves the clarification is added
        // context rather than a replacement — with no CallSession in between to obscure which
        // provider did what.
        StubSession session = new();
        var clarifications = new Clarifications();
        clarifications.Update("applies_to", s => s.Pending = ["ct900", "ct900ent"]);

        var knowledgeProvider = KnowledgeProviderFactory.Create(
            new StubKnowledgePort([Card("a")]),
            new ResolvedKnowledge(KnowledgeMode.Prefetch, Limit: 5, Citations: false, Scoped: false),
            "agent-under-test",
            new SourceLocatorCitationFormatter(),
            loggers: null);
        var clarificationProvider = Provider(maxAsks: 2, maxCandidates: 6, ("applies_to", AppliesToDescription));

        using var scope = OpenTurn(session, State(("applies_to", AppliesToDescription)), clarifications);

        var afterKnowledge = await knowledgeProvider.InvokingAsync(
            Invoking(session, "the screen says e33"), TestContext.Current.CancellationToken);
        Assert.Contains(afterKnowledge.Messages!, message => message.Text.Contains("card a", StringComparison.Ordinal));

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        var merged = await clarificationProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(StubAgent.Instance, session, afterKnowledge),
            TestContext.Current.CancellationToken);
#pragma warning restore MAAI001

        Assert.Contains(merged.Messages!, message => message.Text.Contains("card a", StringComparison.Ordinal));
        Assert.Contains(
            merged.Messages!,
            message => message.Text.Contains("is not yet known", StringComparison.Ordinal));
    }

    private static ClarificationProvider Provider(
        int maxAsks, int maxCandidates, params (string Slot, string Description)[] slots)
        => new(
            new KnowledgeAmbiguityConfiguration { MaxAsks = maxAsks, MaxCandidates = maxCandidates },
            [.. slots.Select(slot => slot.Slot)],
            slots.ToDictionary(slot => slot.Slot, string? (slot) => slot.Description, StringComparer.Ordinal));

    private static StateDocument State(params (string Slot, string Description)[] slots)
    {
        Dictionary<string, StateSlotConfiguration> declared = new(StringComparer.Ordinal);
        foreach (var (slot, description) in slots)
        {
            declared[slot] = new StateSlotConfiguration
            {
                Type = StateSlotType.String,
                Writer = StateWriter.Extractor,
                Description = description,
            };
        }

        var configuration = new AgentCoreConfiguration
        {
            ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
            Name = "clarification-provider-tests",
            State = declared,
        };

        return new StateDocument(configuration);
    }

    private static Scope OpenTurn(AgentSession session, StateDocument state, Clarifications clarifications)
    {
        var outer = TurnContextScope.Enter(new TurnContext { Session = session });
        var inner = TurnAmbients.Amend(a => a with { State = state, Clarifications = clarifications });
        return new Scope(outer, inner);
    }

    /// <summary>
    /// Runs the provider the way the framework runs it, over an otherwise empty context — so
    /// <c>context.Messages</c> on the result is exactly what this provider itself contributed, with
    /// no caller message riding along to make a null-versus-empty assertion pass by accident.
    /// </summary>
    private static AIContextProvider.InvokingContext Invoking(AgentSession session)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        return new(StubAgent.Instance, session, new AIContext());
#pragma warning restore MAAI001
    }

    /// <summary>Runs the provider with a caller message in view, for the knowledge provider's query.</summary>
    private static AIContextProvider.InvokingContext Invoking(AgentSession session, string text)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        return new(StubAgent.Instance, session, new AIContext { Messages = [new ChatMessage(ChatRole.User, text)] });
#pragma warning restore MAAI001
    }

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

    private sealed class Scope : IDisposable
    {
        private readonly IDisposable _outer;
        private readonly IDisposable _inner;

        public Scope(IDisposable outer, IDisposable inner)
        {
            _outer = outer;
            _inner = inner;
        }

        public void Dispose()
        {
            _inner.Dispose();
            _outer.Dispose();
        }
    }

    private sealed class StubSession : AgentSession;

    /// <summary>Stands in for the agent the framework names on a context. Nothing here runs it.</summary>
    private sealed class StubAgent : AIAgent
    {
        public static StubAgent Instance { get; } = new();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default)
            => new(new StubSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

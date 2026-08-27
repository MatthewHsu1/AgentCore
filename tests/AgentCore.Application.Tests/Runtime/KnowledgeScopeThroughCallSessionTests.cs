using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The one wiring a host can actually write: open a knowledge scope, run a turn, and have the scope
/// still be there when the retrieval delegate reads it.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of the scope opens it and reads it back on the same flow, with no turn loop in
/// between, and every one of them passed while the feature was wired shut: <c>TurnAmbients.Enter</c>
/// pushed a fresh record carrying only the four ambients the turn loop owns, so the fifth — the one
/// the HOST opens — was erased before the first model call and again on every streaming step. The
/// defect lives in the interaction, so the test has to run the interaction.
/// </para>
/// <para>
/// The agent here is <c>scoped: true</c>, which makes the erasure visible twice over: the provider's
/// own gate refuses to call the store at all with no scope open, so a broken carry-through shows up
/// as a store nobody searched as well as an ambient nobody could read.
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

    [Fact]
    public async Task RunTurnAsync_UnderAHostScope_TheStoreStillSeesThatScope()
    {
        using SequencedChatClient reply = new("hello there.");
        var port = new StubKnowledgePort([Card("a")]);
        var session = Build(reply, port).Create("call-1");

        var scope = Scope();
        using (KnowledgeScopeScope.Open(scope))
        {
            await session.RunTurnAsync("the screen says e33", TestContext.Current.CancellationToken);
        }

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
        using (KnowledgeScopeScope.Open(scope))
        {
            await foreach (var update in session
                .RunTurnStreamingAsync("the screen says e33", TestContext.Current.CancellationToken))
            {
                Assert.NotNull(update);
            }
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
        using (KnowledgeScopeScope.Open(scope))
        {
            await session.RunTurnAsync("the screen says e33", TestContext.Current.CancellationToken);

            Assert.Same(scope, KnowledgeScopeScope.Current);
        }

        Assert.Null(KnowledgeScopeScope.Current);
    }

    private static CallSessionFactory Build(SequencedChatClient reply, StubKnowledgePort port)
    {
        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(Yaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { Knowledge = port });

        return new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
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

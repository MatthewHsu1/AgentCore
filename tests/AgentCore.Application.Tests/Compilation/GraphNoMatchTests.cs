using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// Row 4 of the section 8.2 compile table, where no edge guard is true.
/// </summary>
/// <remarks>
/// <para>
/// A stage says <c>onNoMatch: error</c>, and a graph node has no such key. Measured on
/// <c>Microsoft.Agents.AI.Workflows</c> 1.17.0: a run whose outgoing guards are all false ends at
/// idle, <c>InProcessRunner</c> raises no exception, and the <c>AsAIAgent()</c> wrapper emits no
/// update. The caller therefore reads an empty run and no error, which is the silent graph failure
/// section 8.2 refuses to ship.
/// </para>
/// <para>
/// Every test here runs offline. There is no network call and no API key in this file.
/// </para>
/// </remarks>
public sealed class GraphNoMatchTests
{
    /// <summary>The text the one output node produces when the run reaches it.</summary>
    private const string EscalatedReply = "ESCALATED";

    /// <summary>
    /// One start node and one guarded exit. The guard is false whenever <c>escalate</c> is false, so
    /// the run stops at the start node and nothing reaches the output node.
    /// </summary>
    private const string NoMatchGraphYaml =
        """
        apiVersion: agentcore/v1
        name: no-match-graph
        state:
          escalate: { type: boolean, writer: extractor, default: false }
        guards:
          wants_human: { "===": [ { var: escalate }, true ] }
        agents:
          items:
            - { id: router, model: { ref: router } }
            - { id: human, model: { ref: human } }
        graph:
          nodes:
            - { id: route, agent: router, start: true }
            - { id: escalated, agent: human, output: true }
          edges:
            - { from: route, to: escalated, when: wants_human }
        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: router }
            - { kind: openai, model: gpt-4.1-mini, as: human }
        """;

    [Fact]
    public async Task Row4_AGraphWhereNoEdgeGuardIsTrue_ThrowsInsteadOfAnsweringNothing()
    {
        using Harness harness = new();
        using var scope = CallStateScope.Enter(harness.NewState(escalate: false));

        var failure = await Record.ExceptionAsync(() => harness.Compiled.Agent.RunAsync(
            "hello", cancellationToken: TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("no-match-graph", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ConfigurationCompiler.NoOutputMessage, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row4_AGraphWhereNoEdgeGuardIsTrue_ThrowsWhileStreamingToo()
    {
        using Harness harness = new();
        using var scope = CallStateScope.Enter(harness.NewState(escalate: false));

        // The streaming path hands the updates over one at a time, so the check reads them as they
        // pass and throws after the last one. A caller that enumerates to the end sees the fault.
        var failure = await Record.ExceptionAsync(async () =>
        {
            await foreach (var update in harness.Compiled.Agent.RunStreamingAsync(
                "hello", cancellationToken: TestContext.Current.CancellationToken))
            {
                _ = update;
            }
        });

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("no-match-graph", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row4_AGraphWhereAnEdgeGuardIsTrue_StillAnswers()
    {
        using Harness harness = new();
        using var scope = CallStateScope.Enter(harness.NewState(escalate: true));

        var reply = await harness.Compiled.Agent.RunAsync(
            "hello", cancellationToken: TestContext.Current.CancellationToken);

        // The check sits in front of nothing. A run that produced text is the run it always was.
        Assert.Contains(EscalatedReply, reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row4_AGraphWhereAnEdgeGuardIsTrue_StillStreams()
    {
        using Harness harness = new();
        using var scope = CallStateScope.Enter(harness.NewState(escalate: true));

        List<string> text = [];
        await foreach (var update in harness.Compiled.Agent.RunStreamingAsync(
            "hello", cancellationToken: TestContext.Current.CancellationToken))
        {
            text.Add(update.Text);
        }

        Assert.Contains(EscalatedReply, string.Concat(text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row4_ANoMatchInsideACall_SpeaksTheFallbackAndTheCallLives()
    {
        using Harness harness = new();
        var session = harness.NewSession();
        session.State.TryWrite("escalate", false);

        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Section 8.7, sixth row. The run throws, the turn speaks the fallback, and the call lives.
        // A live call therefore does not change: only a host that runs the compiled agent itself
        // reads the exception.
        Assert.Equal(CallSession.FallbackReply, turn.ReplyText);
        Assert.NotNull(turn.Failure);
        Assert.False(session.IsComplete);
    }

    [Fact]
    public async Task Row4_ANoMatchInsideAStreamingCall_SpeaksTheFallbackAndTheCallLives()
    {
        using Harness harness = new();
        var session = harness.NewSession();
        session.State.TryWrite("escalate", false);

        await foreach (var update in session.RunTurnStreamingAsync(
            "hello", TestContext.Current.CancellationToken))
        {
            _ = update;
        }

        Assert.NotNull(session.LastTurn);
        Assert.Equal(CallSession.FallbackReply, session.LastTurn!.ReplyText);
        Assert.NotNull(session.LastTurn.Failure);
        Assert.False(session.IsComplete);
    }

    /// <summary>
    /// One compiled graph, wired the way the composition root wires it.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        // The start node classifies and speaks nothing, which is the shape that goes silent. Every
        // node binding emits agent update events, so a start node that did speak would carry its own
        // text out of the run and the caller would read that instead of an empty run.
        private readonly ScriptedChatClient _router = new(string.Empty);
        private readonly ScriptedChatClient _human = new(EscalatedReply);
        private readonly AgentCoreConfiguration _document;
        private readonly CallSessionFactory _sessions;

        /// <summary>Compiles the no-match graph once, over two offline models.</summary>
        public Harness()
        {
            _document = ConfigurationLoader.LoadYaml(NoMatchGraphYaml);
            GuardEvaluator guards = new(_document.Guards);

            RoutingFactory models = new();
            models.Route("router", _router);
            models.Route("human", _human);

            Compiled = ConfigurationCompiler.Compile(
                _document,
                new AgentCompilationContext(models)
                {
                    Guards = guards,
                    StateSnapshot = CallStateScope.Snapshot,
                });

            _sessions = new CallSessionFactory(Compiled, guards);
        }

        /// <summary>Gets the one compiled agent every call shares.</summary>
        public CompiledAgent Compiled { get; }

        /// <summary>Builds the state of one call, with the slot the guard reads set.</summary>
        /// <param name="escalate">What the guard <c>wants_human</c> reads.</param>
        /// <returns>The state document.</returns>
        public StateDocument NewState(bool escalate)
        {
            StateDocument state = new(_document);
            state.TryWrite("escalate", escalate);
            return state;
        }

        /// <summary>Builds the session of one more call.</summary>
        /// <returns>The session.</returns>
        public CallSession NewSession() => _sessions.Create();

        public void Dispose()
        {
            _router.Dispose();
            _human.Dispose();
        }
    }

    /// <summary>Hands one offline client to each <c>providers.llm[].as</c> name.</summary>
    private sealed class RoutingFactory : IChatClientFactory
    {
        private readonly Dictionary<string, IChatClient> _byName = new(StringComparer.Ordinal);

        /// <summary>Binds one client to one model name.</summary>
        /// <param name="name">The <c>as</c> name of one <c>providers.llm</c> entry.</param>
        /// <param name="client">The client that answers for it.</param>
        public void Route(string name, IChatClient client) => _byName[name] = client;

        public IChatClient GetChatClient(ModelReference? model)
            => model is not null && _byName.TryGetValue(model.Ref, out var client)
                ? client
                : throw new KeyNotFoundException($"No offline client is routed to the model '{model?.Ref}'.");
    }
}

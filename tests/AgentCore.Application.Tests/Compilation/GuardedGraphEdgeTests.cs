using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// Row 4 of the section 8.2 compile table, with a guarded edge.
/// </summary>
/// <remarks>
/// <para>
/// Two rules pull apart here. T44 makes the compiled agent a process singleton, so one graph serves
/// every call and no code path compiles one for each call. A guarded edge reads the state of one
/// call, and each call owns its own state document. <see cref="CallStateScope"/> is the seam that
/// holds both: the predicate captures nothing and asks the flow of execution instead.
/// </para>
/// <para>
/// Every test here runs offline. There is no network call and no API key in this file.
/// </para>
/// </remarks>
public sealed class GuardedGraphEdgeTests
{
    private const int FanOut = 26;

    /// <summary>The text of the node a call reaches when the guard <c>wants_human</c> holds.</summary>
    private const string EscalatedReply = "ESCALATED";

    /// <summary>The text of the node a call reaches when the guard <c>stays_with_bot</c> holds.</summary>
    private const string HandledReply = "HANDLED";

    /// <summary>
    /// One start node and two guarded exits. Check 5 of section 8.5 proves the two guards exclusive
    /// over the state domain, so exactly one edge fires for each call.
    /// </summary>
    internal const string GuardedGraphYaml =
        """
        apiVersion: agentcore/v1
        name: guarded-graph
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

    [Fact]
    public void AGuardedEdge_PassesEveryOneOfTheEightChecks()
    {
        var document = ConfigurationLoader.LoadYaml(GuardedGraphYaml);

        // The defect this file closes: the document was valid and then failed to compile. Check 5
        // even proves the two guards exclusive, so the load has nothing left to object to.
        var result = ConfigurationValidator.Evaluate(document);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AGuardedEdge_CompilesAndTakesTheEdgeTheStateNames()
    {
        using Harness harness = new();
        var session = harness.NewSession();
        session.State.TryWrite("escalate", true);

        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Contains(EscalatedReply, turn.ReplyText, StringComparison.Ordinal);
        Assert.DoesNotContain(HandledReply, turn.ReplyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuardedEdge_TakesTheOtherEdgeWhenTheStateSaysSo()
    {
        using Harness harness = new();
        var session = harness.NewSession();
        session.State.TryWrite("escalate", false);

        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.Contains(HandledReply, turn.ReplyText, StringComparison.Ordinal);
        Assert.DoesNotContain(EscalatedReply, turn.ReplyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuardedEdge_TakesTheEdgeTheStateNamesWhileStreaming()
    {
        using Harness harness = new();
        var session = harness.NewSession();
        session.State.TryWrite("escalate", true);

        List<string> text = [];
        await foreach (var update in session.RunTurnStreamingAsync(
            "hello", TestContext.Current.CancellationToken))
        {
            text.Add(update.Text);
        }

        // An async iterator restores the execution context of its caller at every yield. The turn
        // loop therefore opens the scope once for each round, and this asserts that it does.
        Assert.Contains(EscalatedReply, string.Concat(text), StringComparison.Ordinal);
        Assert.DoesNotContain(HandledReply, string.Concat(text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rule16_TwentySixSimultaneousCalls_EachTakeTheirOwnEdgeThroughOneCompiledGraph()
    {
        using Harness harness = new();
        var token = TestContext.Current.CancellationToken;
        using Barrier gate = new(FanOut);

        var calls = Enumerable.Range(0, FanOut).Select(index => Task.Run(
            async () =>
            {
                var escalate = index % 2 == 0;
                var session = harness.NewSession();
                session.State.TryWrite("escalate", escalate);

                // Nothing starts until all 26 are ready, so the fan-out is really simultaneous.
                gate.SignalAndWait(token);

                var turn = await session.RunTurnAsync($"call {index}", token).ConfigureAwait(false);
                return (Escalate: escalate, turn.ReplyText);
            },
            token));

        var results = await Task.WhenAll(calls);

        // Rule 16: one compiled agent, and no code path compiles one for each call.
        Assert.Equal(1, harness.CompileCount);

        // No call read the state of another one. Thirteen went each way, and none went both.
        Assert.All(results, result => Assert.Contains(
            result.Escalate ? EscalatedReply : HandledReply, result.ReplyText, StringComparison.Ordinal));
        Assert.All(results, result => Assert.DoesNotContain(
            result.Escalate ? HandledReply : EscalatedReply, result.ReplyText, StringComparison.Ordinal));
        Assert.Equal(FanOut / 2, results.Count(result => result.Escalate));
    }

    [Fact]
    public async Task AGuardedEdgeWithNoAmbientState_FailsLoudlyAndNeverAnswersFalse()
    {
        using Harness harness = new();

        // The run goes straight at the shared graph, with no call scope anywhere. A guarded edge that
        // quietly became unconditional is the silent graph failure section 8.2 refuses to ship, so
        // the predicate throws instead.
        var failure = await Record.ExceptionAsync(() => harness.Compiled.Agent.RunAsync(
            "hello", cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotNull(failure);
        Assert.Contains(CallStateScope.NoScopeMessage, Flatten(failure), StringComparison.Ordinal);
    }

    [Fact]
    public void AGuardedGraph_KeepsTheNameOfTheDocument()
    {
        using Harness harness = new();

        // The check that makes a missing state source loud sits in front of the graph, and it must
        // not change what the graph is. Row 4 still names the document.
        Assert.Equal("guarded-graph", harness.Compiled.Agent.Name);
        Assert.Equal(CompiledAgentShape.ExplicitGraph, harness.Compiled.Shape);
    }

    [Fact]
    public async Task Rule13_AGuardedGraph_YieldsItsFirstDeltaBeforeTheModelFinishes()
    {
        using Harness harness = new(holdEscalatedReply: true);
        var session = harness.NewSession();
        session.State.TryWrite("escalate", true);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, TestContext.Current.CancellationToken);

        string? first = null;
        await foreach (var update in session.RunTurnStreamingAsync("hello", linked.Token))
        {
            if (update.Text is not { Length: > 0 } text)
            {
                continue;
            }

            first = text;

            // The reply reached us, so let the model finish. The order proves the seam still streams
            // with the guard check in front of it.
            harness.ReleaseEscalatedReply();
            break;
        }

        harness.ReleaseEscalatedReply();

        // The delta arrives before the run finishes, which is what rule 13 asks. It is the OUTPUT
        // node's word and not the start node's: the caller hears the answer, never the graph
        // deliberating its way to one, on the streaming path exactly as on the buffered one.
        Assert.Equal("ESCALATED", first);
    }

    [Fact]
    public void AGuardedEdgeWithNoStateSource_IsALoadTimeErrorThatNamesTheMissingSeam()
    {
        var document = ConfigurationLoader.LoadYaml(GuardedGraphYaml);
        using ScriptedChatClient client = new("ok");
        AgentCompilationContext context = new(new FakeChatClientFactory(client))
        {
            Guards = new GuardEvaluator(document.Guards),
        };

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationCompiler.Compile(document, context));

        Assert.Equal("/graph/edges/0/when", failure.Pointer);
        Assert.Contains("binds no state source", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGuardedEdgeWithNoEvaluator_IsALoadTimeErrorThatNamesTheMissingSeam()
    {
        var document = ConfigurationLoader.LoadYaml(GuardedGraphYaml);
        using ScriptedChatClient client = new("ok");
        AgentCompilationContext context = new(new FakeChatClientFactory(client))
        {
            StateSnapshot = CallStateScope.Snapshot,
        };

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationCompiler.Compile(document, context));

        Assert.Equal("/graph/edges/0/when", failure.Pointer);
        Assert.Contains("binds no guard evaluator", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Reads one fault and every fault it wraps, as one string.</summary>
    /// <param name="exception">The fault the run reported.</param>
    /// <returns>Every message in the chain, joined.</returns>
    private static string Flatten(Exception exception)
    {
        List<string> messages = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(Flatten));
            }
        }

        return string.Join(" | ", messages);
    }

    /// <summary>
    /// One compiled graph, wired the way the composition root wires it.
    /// </summary>
    /// <remarks>
    /// The seams are the two <c>AddAgentCore</c> binds: the shared guard evaluator, and
    /// <see cref="CallStateScope.Snapshot"/> as the state source. Nothing per call is captured.
    /// </remarks>
    private sealed class Harness : IDisposable
    {
        private readonly ScriptedChatClient _router = new("ROUTED");
        private readonly ScriptedChatClient _human;
        private readonly ScriptedChatClient _bot = new(HandledReply);
        private readonly CompiledAgentRegistry _registry = new();
        private readonly CallSessionFactory _sessions;

        /// <summary>Compiles the guarded graph once, over three offline models.</summary>
        /// <param name="holdEscalatedReply">
        /// Whether the escalated node holds every fragment after the first. A test that asks for this
        /// proves the reply reaches the caller before the model finishes.
        /// </param>
        public Harness(bool holdEscalatedReply = false)
        {
            _human = new ScriptedChatClient(EscalatedReply, " and", " more.")
            {
                GateAfterFirstFragment = holdEscalatedReply,
            };

            var document = ConfigurationLoader.LoadYaml(GuardedGraphYaml);
            GuardEvaluator guards = new(document.Guards);

            RoutingFactory models = new();
            models.Route("router", _router);
            models.Route("human", _human);
            models.Route("bot", _bot);

            Compiled = _registry.GetOrCompile(
                document,
                new AgentCompilationContext(models)
                {
                    Guards = guards,
                    StateSnapshot = CallStateScope.Snapshot,
                });

            _sessions = new CallSessionFactory(Compiled, guards);
        }

        /// <summary>Gets the one compiled agent every call shares.</summary>
        public CompiledAgent Compiled { get; }

        /// <summary>Gets how many times the registry ran the compile table.</summary>
        public int CompileCount => _registry.CompileCount;

        /// <summary>Builds the session of one more call.</summary>
        /// <returns>The session.</returns>
        public CallSession NewSession() => _sessions.Create();

        /// <summary>Lets the rest of the escalated reply flow.</summary>
        public void ReleaseEscalatedReply() => _human.OpenGate();

        public void Dispose()
        {
            _router.Dispose();
            _human.Dispose();
            _bot.Dispose();
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

using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Diagnostics;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Audit;

/// <summary>
/// The turn loop writes the audit chain of D23, and the sink never sits on the turn.
/// </summary>
/// <remarks>
/// The session is the only place that knows the turn index, both stages, and the sequence a later
/// amendment must reference, so it is the only place that produces an event.
/// </remarks>
public sealed class CallSessionAuditTests
{
    private const string PolicyYaml =
        """
        apiVersion: agentcore/v1
        name: audited
        state:
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
        guards:
          saidGoodbye: { var: callerSaidGoodbye }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller" }
            - { id: closer,  instructions: "close the call" }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: greeter, to: [ { stage: close, when: saidGoodbye } ] }
            - { id: close,    agent: closer,  terminal: true }
        """;

    private const string ToolYaml =
        """
        apiVersion: agentcore/v1
        name: audited-tools
        tools:
          - { id: lookup_order, kind: builtin, uses: orders.read }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        """;

    private const string StayingNull = """{ "callerSaidGoodbye": null }""";
    private const string SaidGoodbye = """{ "callerSaidGoodbye": true }""";

    // -------------------------------------------------------------------------------------------
    // The events, and the sequence a later amendment references.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public void ANewSession_WritesTheCallStartedEventAtSequenceZero()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        InMemoryAuditSink sink = new();

        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        var started = Assert.Single(sink.EventsOf("call-1"));
        Assert.Equal(AuditEventKind.CallStarted, started.Kind);
        Assert.Equal(0, started.Sequence);
        Assert.Null(started.TurnIndex);
        Assert.Null(started.AmendsSequence);
        Assert.Equal(session.CallId, started.CallId);
    }

    [Fact]
    public async Task AFinishedTurn_WritesOneTurnCompletedEventWithBothStages()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        InMemoryAuditSink sink = new();
        TestTimeProvider clock = new();
        var session = Build(PolicyYaml, reply, fill, timeProvider: clock, auditSink: sink).Create("call-1");

        clock.Advance(TimeSpan.FromSeconds(12));
        var turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var completed = Assert.Single(sink.EventsOf("call-1"), item => item.Kind == AuditEventKind.TurnCompleted);
        Assert.Equal(1, completed.Sequence);
        Assert.Equal(0, completed.TurnIndex);
        Assert.Equal("hello there.", completed.Payload[AuditPayloadKeys.ReplyText]);
        Assert.Equal("greeting", completed.Payload[AuditPayloadKeys.StageBefore]);
        Assert.Equal("greeting", completed.Payload[AuditPayloadKeys.StageAfter]);

        // The moment comes from the injected clock and not from the sink. A background writer would
        // stamp it one enqueue late.
        Assert.Equal(clock.GetUtcNow(), completed.OccurredAt);
        Assert.Equal(completed.OccurredAt, turn.EndedAt);
    }

    [Fact]
    public async Task ABargeIn_WritesASecondEventThatAmendsTheTurnEvent()
    {
        using ScriptedChatClient reply = new("hello", " there.") { GateAfterFirstFragment = true };
        using SequencedChatClient fill = new(StayingNull);
        InMemoryAuditSink sink = new();
        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        var running = session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
        Assert.True(session.Interrupt("hel", TimeSpan.FromMilliseconds(120)));
        await running;

        var events = sink.EventsOf("call-1");
        var completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
        var cut = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);

        // T23: the chain is append-only, so an amendment is a second event that references the first.
        Assert.Equal(completed.Sequence, cut.AmendsSequence);
        Assert.True(cut.Sequence > completed.Sequence);
        Assert.Equal(completed.TurnIndex, cut.TurnIndex);

        // Item 6a: the event records the text the caller ACTUALLY HEARD. Nothing here is estimated,
        // because the relay reported both values on its interrupt frame.
        Assert.Equal("hel", cut.Payload[AuditPayloadKeys.UtteranceUntilInterrupt]);
        Assert.Equal("120", cut.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
    }

    [Fact]
    public async Task TheFourthConsecutiveToolFailure_WritesAToolFailedEventBeforeTheTurnEvent()
    {
        using LoopingToolCallingChatClient reply = new();
        InMemoryAuditSink sink = new();
        var session = Build(ToolYaml, reply, null, new ThrowingToolFactory(), auditSink: sink).Create("call-1");

        await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        var events = sink.EventsOf("call-1");
        var failed = Assert.Single(events, item => item.Kind == AuditEventKind.ToolFailed);
        var completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);

        Assert.True(failed.Sequence < completed.Sequence);
        Assert.Equal(0, failed.TurnIndex);
        Assert.Contains(ThrowingToolFactory.Message, failed.Payload[AuditPayloadKeys.ToolError], StringComparison.Ordinal);

        // The fault threw out of the run, so no tool is named. A missing fact is an absent key.
        Assert.False(failed.Payload.ContainsKey(AuditPayloadKeys.ToolName));
    }

    [Fact]
    public async Task ATerminalStage_ClosesTheChainWithOneCallEndedEvent()
    {
        using SequencedChatClient reply = new("goodbye.");
        using SequencedChatClient fill = new(SaidGoodbye);
        InMemoryAuditSink sink = new();
        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

        Assert.True(session.IsComplete);
        var ended = Assert.Single(sink.EventsOf("call-1"), item => item.Kind == AuditEventKind.CallEnded);
        Assert.Null(ended.TurnIndex);

        // The reason is one token of the closed set, so a report can count it. The terminal stage is
        // detail beside it, and it is not the reason.
        Assert.Equal("agent.completed", ended.Payload[AuditPayloadKeys.EndReason]);
        Assert.Equal("close", ended.Payload[AuditPayloadKeys.StageAfter]);

        // A hang-up frame that arrives after the machine already closed the call writes nothing.
        Assert.False(session.EndCall(CallEndReason.CallerHungUp));
    }

    [Fact]
    public void AHostThatEndsTheCall_ClosesTheChainOnce()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        InMemoryAuditSink sink = new();
        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        Assert.True(session.EndCall(CallEndReason.CallerHungUp));
        Assert.False(session.EndCall(CallEndReason.CallerHungUp));

        var ended = Assert.Single(sink.EventsOf("call-1"), item => item.Kind == AuditEventKind.CallEnded);
        Assert.Equal("caller.hangup", ended.Payload[AuditPayloadKeys.EndReason]);

        // The machine never ran, so no stage closed the call and no stage rides on the event.
        Assert.False(ended.Payload.ContainsKey(AuditPayloadKeys.StageAfter));
        Assert.True(session.IsComplete);
    }

    /// <summary>Section 11, item 5: the call goes to a human through the conference pattern.</summary>
    [Fact]
    public void ATransferToAHuman_ClosesTheChainWithItsOwnReason()
    {
        using SequencedChatClient reply = new("one moment please.");
        using SequencedChatClient fill = new(StayingNull);
        InMemoryAuditSink sink = new();
        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        // The adapter joins the call to a conference and never sends the transfer command (T27).
        Assert.True(session.EndCall(CallEndReason.TransferredToHuman));

        var ended = Assert.Single(sink.EventsOf("call-1"), item => item.Kind == AuditEventKind.CallEnded);
        Assert.Equal("agent.transferred", ended.Payload[AuditPayloadKeys.EndReason]);
    }

    [Fact]
    public void AReasonOutsideTheClosedSet_EndsNoCall()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        InMemoryAuditSink sink = new();
        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        Assert.Throws<ArgumentOutOfRangeException>(() => session.EndCall((CallEndReason)99));

        // Nothing moved. The call still runs, and the chain still holds only its first event.
        Assert.False(session.IsComplete);
        Assert.DoesNotContain(sink.EventsOf("call-1"), item => item.Kind == AuditEventKind.CallEnded);
    }

    // -------------------------------------------------------------------------------------------
    // Item 6: the chain the events form verifies.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheEventsOfOneCall_FormAChainThatVerifies()
    {
        using SequencedChatClient reply = new("hello there.", "goodbye.");
        using SequencedChatClient fill = new(StayingNull, SaidGoodbye);
        InMemoryAuditSink sink = new();
        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        var token = TestContext.Current.CancellationToken;
        await session.RunTurnAsync("hi", token);
        await session.RunTurnAsync("goodbye", token);

        var events = sink.EventsOf("call-1");

        // The sequence is monotonic and starts at zero, which is what an amendment references and
        // what orders two events inside one millisecond.
        Assert.Equal([0L, 1L, 2L, 3L], events.Select(item => item.Sequence).ToArray());

        // This is chain_check of section 11, item 6, run over what the turn loop produced.
        Assert.True(AuditChain.Verify(AuditChain.LinkAll(events)).IsIntact);
    }

    // -------------------------------------------------------------------------------------------
    // D23: the write never sits on the turn.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheTurn_FinishesWhileTheAppendIsStillOpen()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        BlockingAuditSink sink = new();
        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        // Section 7: a durable insert costs 13 ms at p50 and 32 ms at p99, against 91 nanoseconds to
        // enqueue. The turn therefore finishes with no append complete at all.
        var turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal("hello there.", turn.ReplyText);

        // The whole turn ran and returned while call.started is still inside the sink. The
        // turn.completed event is queued behind it rather than racing past it, because the events of
        // one call reach an observer in the order the call raised them, so exactly one has arrived.
        Assert.Single(sink.Events);

        sink.Release();

        // Off the turn, and only now. The queued event lands on the dispatcher's own task, which is
        // where a slow sink is paid for.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (sink.Events.Count < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.TurnCompleted],
            sink.Events.Select(item => item.Kind).ToArray());
    }

    [Fact]
    public async Task ASinkThatRefusesAnEvent_IsLoggedAndTheTurnGoesOn()
    {
        RecordingLogger logger = new();
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        var session = Build(PolicyYaml, reply, fill, auditSink: new ThrowingAuditSink(), logger: logger)
            .Create("call-1");

        var turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // Audit is a record of the call and never a part of it.
        Assert.Equal("hello there.", turn.ReplyText);
        Assert.NotEmpty(logger.Of(5));
        Assert.All(logger.Of(5), line => Assert.Equal(LogLevel.Error, line.Level));
    }

    [Fact]
    public async Task ASessionWithNoSink_RunsATurnAndThrowsNothing()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        var session = Build(PolicyYaml, reply, fill).Create("call-1");

        var turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal("hello there.", turn.ReplyText);
    }

    // -------------------------------------------------------------------------------------------
    // The in-memory sink.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheInMemorySink_KeepsTheCallsApart()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        InMemoryAuditSink sink = new();
        var factory = Build(PolicyYaml, reply, fill, auditSink: sink);

        var first = factory.Create("call-1");
        var second = factory.Create("call-2");
        await first.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // One sink serves every call, because a session names itself on every event.
        Assert.Equal(2, sink.EventsOf("call-1").Count);
        Assert.Single(sink.EventsOf("call-2"));
        Assert.Equal(3, sink.Events.Count);
        Assert.Equal("call-2", second.CallId);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    private static CallSessionFactory Build(
        string yaml,
        IChatClient reply,
        IChatClient? fill,
        IAgentToolFactory? tools = null,
        TimeProvider? timeProvider = null,
        IAuditSinkPort? auditSink = null,
        ILogger? logger = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(reply);
        if (fill is not null)
        {
            chatClients.Route("fill", fill);
        }

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { Tools = tools });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            timeProvider,
            logger,
            moderation: null,
            CallObservers.Standard(auditSink, logger));
    }
}

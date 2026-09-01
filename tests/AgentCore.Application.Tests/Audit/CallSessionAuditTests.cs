using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
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
/// The session is the only place that knows the turn index, both stages, and the identity a later
/// amendment must name, so it is the only place that produces an event.
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
          - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        """;

    private const string StayingNull = """{ "callerSaidGoodbye": null }""";
    private const string SaidGoodbye = """{ "callerSaidGoodbye": true }""";

    // -------------------------------------------------------------------------------------------
    // The events, and the identity a later amendment names.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public void ANewSession_WritesTheCallStartedEvent()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        InMemoryAuditSink sink = new();

        var session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("call-1");

        var started = Assert.Single(sink.EventsOf("call-1"));
        Assert.Equal(AuditEventKind.CallStarted, started.Kind);
        Assert.NotEqual(Guid.Empty, started.EventId);
        Assert.Null(started.TurnIndex);
        Assert.Null(started.AmendsEventId);
        Assert.Equal(session.CallId, started.CallId);
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_CollidesWithNothing()
    {
        using SequencedChatClient reply = new("hello there.", "still here.");
        using SequencedChatClient fill = new(StayingNull, StayingNull);
        InMemoryAuditSink sink = new();

        // One factory, so both sessions share one compiled agent and one store — which is what a host
        // holds, and what makes the second session a resume rather than a different call.
        var factory = Build(PolicyYaml, reply, fill, auditSink: sink);

        var first = factory.Create("call-1");
        await first.RunTurnAsync("hello", TestContext.Current.CancellationToken);
        await first.FlushTranscriptAsync();

        var second = factory.Create("call-1");
        await second.RunTurnAsync("still there?", TestContext.Current.CancellationToken);
        await second.FlushTranscriptAsync();

        var written = sink.EventsOf("call-1");

        // The shape is the assertion, and both halves of it are the point.
        //
        // ARRIVAL: before this change the second session restarted its counter at zero, store 3
        // refused every event it raised, and a resumed call lost its whole audit trail. The proof of
        // the fix is that the second session's two events are HERE — distinctness proves nothing,
        // because InMemoryAuditSink enforces no key and Guid.CreateVersion7 is unique by
        // construction, so that assertion passed just as well over the two events that never arrived.
        //
        // A SECOND call.started: a session opening onto a call that already has words raises one, and
        // it is kept rather than suppressed. See AuditEventKind.CallStarted for why. This is where
        // that decision is visible, so a build that quietly stopped raising it fails here.
        Assert.Equal(
            [
                AuditEventKind.CallStarted,
                AuditEventKind.TurnCompleted,
                AuditEventKind.CallStarted,
                AuditEventKind.TurnCompleted,
            ],
            written.Select(item => item.Kind).ToArray());

        var ids = written.Select(item => item.EventId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
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
        Assert.Same(completed, sink.EventsOf("call-1")[1]);
        Assert.Equal(0, completed.TurnIndex);
        Assert.Equal(AuditHash.OfText("hello there.").Value, completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
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

        // The streaming shape, because only an audible turn is cut in flight: the caller heard the
        // first fragment, then spoke over the rest. A turn that never streamed is never audible and
        // its frame amends the turn before it instead — see AnInterruption_NeverCutsATurnThatDoesNotStream.
        await using (var updates = session
            .RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await updates.MoveNextAsync());
            Assert.True(session.Interrupt("hel", TimeSpan.FromMilliseconds(120)));
            Assert.False(await updates.MoveNextAsync());
        }

        var events = sink.EventsOf("call-1");
        var completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
        var cut = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);

        // T23: the chain is append-only, so an amendment is a second event that references the first.
        var chain = sink.EventsOf("call-1");
        Assert.Equal(completed.EventId, cut.AmendsEventId);
        Assert.True(chain.ToList().IndexOf(cut) > chain.ToList().IndexOf(completed));
        Assert.Equal(completed.TurnIndex, cut.TurnIndex);

        // Item 6a: the event records the text the caller ACTUALLY HEARD. Nothing here is estimated,
        // because the relay reported both values on its interrupt frame.
        Assert.Equal(AuditHash.OfText("hel").Value, cut.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
        Assert.Equal("120", cut.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
    }

    [Fact]
    public async Task TheFourthConsecutiveToolFailure_WritesAToolFailedEventBeforeTheTurnEvent()
    {
        using LoopingToolCallingChatClient reply = new();
        InMemoryAuditSink sink = new();
        var session = Build(ToolYaml, reply, null, new ThrowingToolBuilder().Create, auditSink: sink).Create("call-1");

        await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        var events = sink.EventsOf("call-1");
        var failures = events.Where(item => item.Kind == AuditEventKind.ToolFailed).ToArray();
        var completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);

        // The kind is raised at two altitudes and both are here: one row for each call that failed,
        // and one for the turn that ended because of them. Five in all, and every one of them before
        // the turn event.
        Assert.Equal(5, failures.Length);
        var order = sink.EventsOf("call-1").ToList();
        Assert.All(failures, failure => Assert.True(order.IndexOf(failure) < order.IndexOf(completed)));
        Assert.All(failures, failure => Assert.Equal(0, failure.TurnIndex));

        // The turn-level row is the one section 8.7 row six has always written, and it is unchanged:
        // no tool is named, because the fault reaches the session with no function name on it, and a
        // missing fact is an absent key rather than the word "unknown".
        var turnLevel = Assert.Single(
            failures,
            failure => !failure.Payload.ContainsKey(AuditPayloadKeys.ToolCallId));
        Assert.False(turnLevel.Payload.ContainsKey(AuditPayloadKeys.ToolName));
        Assert.Contains(
            ThrowingToolBuilder.Message,
            turnLevel.Payload[AuditPayloadKeys.ToolError],
            StringComparison.Ordinal);

        // The four call-level rows are the new fact, and each one names its tool. The fourth is the
        // one that spent the budget: the framework does not capture that exception at all, so it never
        // reaches CreateResponseMessages, and only the invocation hook sees it.
        var calls = failures.Where(failure => failure.Payload.ContainsKey(AuditPayloadKeys.ToolCallId)).ToArray();
        Assert.Equal(4, calls.Length);
        Assert.All(calls, call => Assert.Equal("lookup_order", call.Payload[AuditPayloadKeys.ToolName]));
        Assert.All(
            calls,
            call => Assert.Equal(
                ToolFailureKinds.ToToken(ToolFailureKind.Faulted),
                call.Payload[AuditPayloadKeys.ToolFailureKind]));
        Assert.All(
            calls,
            call => Assert.Contains(
                ThrowingToolBuilder.Message,
                call.Payload[AuditPayloadKeys.ToolError],
                StringComparison.Ordinal));

        // Four calls, four ids, and none of them repeated. The name alone could never have told them
        // apart, and the chain used to hold neither.
        var ids = calls.Select(call => call.Payload[AuditPayloadKeys.ToolCallId]).ToArray();
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());

        // The chain still verifies over every one of the new keys.
        Assert.All(events, AuditEventVocabulary.Validate);
    }

    // -------------------------------------------------------------------------------------------
    // The facts of one tool call. §8.7 row six is the turn-level fact; these are the call-level ones.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AToolThatFaults_NamesTheToolAndTheCallIdInTheChain()
    {
        // One failing round, then the model answers, so the turn ends normally and the only thing
        // that could ever have recorded the tool is the observer.
        using NamedToolCallingChatClient reply = new("lookup_order", "I could not reach the order system.");
        InMemoryAuditSink sink = new();
        UnreachableEndpointToolBuilder tools = new();
        var session = Build(ToolYaml, reply, null, tools.Create, auditSink: sink).Create("call-1");

        var turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        var failed = Assert.Single(sink.EventsOf("call-1"), item => item.Kind == AuditEventKind.ToolFailed);
        Assert.Equal("lookup_order", failed.Payload[AuditPayloadKeys.ToolName]);
        Assert.Equal(reply.CallIds[0], failed.Payload[AuditPayloadKeys.ToolCallId]);
        Assert.Equal(
            ToolFailureKinds.ToToken(ToolFailureKind.Faulted),
            failed.Payload[AuditPayloadKeys.ToolFailureKind]);
        Assert.Contains(
            UnreachableEndpointToolBuilder.Message,
            failed.Payload[AuditPayloadKeys.ToolError],
            StringComparison.Ordinal);

        // One failure is under the budget, so the turn is an ordinary turn that spoke.
        Assert.Null(turn.Failure);
        Assert.Equal(0, failed.TurnIndex);
    }

    [Fact]
    public async Task AHallucinatedToolName_ReachesTheChain()
    {
        // The model invented the name. The framework answers it with a message and NO exception, so
        // it spends none of the error budget and the turn goes on — which is correct, and is exactly
        // why nothing else in the system would ever have recorded that it happened.
        using NamedToolCallingChatClient reply = new("lookup_ordar", "Let me try that again.");
        InMemoryAuditSink sink = new();
        var session = Build(ToolYaml, reply, null, new StubToolBuilder("""{"status":"shipped"}""").Create, auditSink: sink)
            .Create("call-1");

        var turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        var failed = Assert.Single(sink.EventsOf("call-1"), item => item.Kind == AuditEventKind.ToolFailed);

        // The name recorded is the name the MODEL called, so a reader that joins it to tools[].id
        // finds nothing — which is the finding.
        Assert.Equal("lookup_ordar", failed.Payload[AuditPayloadKeys.ToolName]);
        Assert.Equal(reply.CallIds[0], failed.Payload[AuditPayloadKeys.ToolCallId]);
        Assert.Equal(
            ToolFailureKinds.ToToken(ToolFailureKind.Undeclared),
            failed.Payload[AuditPayloadKeys.ToolFailureKind]);

        // The turn is unharmed: no exception, no budget spent, and the caller heard a real reply.
        Assert.Null(turn.Failure);
        Assert.Equal("Let me try that again.", turn.ReplyText);
    }

    [Fact]
    public async Task TwoParallelCallsToTheSameTool_AreTwoDistinguishableRecords()
    {
        // One assistant message, two calls, one tool name. Without the call id these are one fact
        // written twice, and a reader cannot tell which call failed.
        using NamedToolCallingChatClient reply = new("lookup_order", "Both lookups failed.", callsPerTurn: 2);
        InMemoryAuditSink sink = new();
        var session = Build(ToolYaml, reply, null, new UnreachableEndpointToolBuilder().Create, auditSink: sink)
            .Create("call-1");

        await session.RunTurnAsync("where are my two orders", TestContext.Current.CancellationToken);

        var failures = sink.EventsOf("call-1").Where(item => item.Kind == AuditEventKind.ToolFailed).ToArray();

        Assert.Equal(2, failures.Length);
        Assert.All(failures, failure => Assert.Equal("lookup_order", failure.Payload[AuditPayloadKeys.ToolName]));
        Assert.Equal(
            reply.CallIds.ToArray(),
            failures.Select(failure => failure.Payload[AuditPayloadKeys.ToolCallId]).Order(StringComparer.Ordinal).ToArray());

        // Two records, and the chain still verifies over them.
        Assert.All(sink.EventsOf("call-1"), AuditEventVocabulary.Validate);
    }

    [Fact]
    public async Task AToolWhoseFaultTheModelCanAnswer_WritesNoToolFailedEventAtAll()
    {
        // The converged design, from the chain's side: the tool ANSWERED. The framework sees a result
        // and no exception, so nothing failed as far as it is concerned and no row is written.
        using NamedToolCallingChatClient reply = new("lookup_order", "That order is already closed.");
        InMemoryAuditSink sink = new();
        var session = Build(ToolYaml, reply, null, new RefusedRequestToolBuilder().Create, auditSink: sink)
            .Create("call-1");

        var turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(sink.EventsOf("call-1"), item => item.Kind == AuditEventKind.ToolFailed);
        Assert.Null(turn.Failure);
        Assert.Equal("That order is already closed.", turn.ReplyText);
    }

    /// <summary>T44: one compiled agent serves every call, so nothing per call may live on it.</summary>
    [Fact]
    public async Task TwoCallsAtOnce_DoNotPolluteEachOthersRecords()
    {
        const int FanOut = 8;
        InMemoryAuditSink sink = new();

        // One factory, one document, one compiled agent behind the sessions — the shape T44 pins.
        using NamedToolCallingChatClient reply = new("lookup_order", "I could not reach the order system.");
        var factory = Build(ToolYaml, reply, null, new UnreachableEndpointToolBuilder().Create, auditSink: sink);

        var token = TestContext.Current.CancellationToken;
        using Barrier gate = new(FanOut);

        await Task.WhenAll(Enumerable.Range(0, FanOut).Select(index => Task.Run(
            async () =>
            {
                var session = factory.Create($"call-{index}");
                gate.SignalAndWait(token);
                await session.RunTurnAsync("where is my order", token).ConfigureAwait(false);
            },
            token)));

        for (var index = 0; index < FanOut; index++)
        {
            var events = sink.EventsOf($"call-{index}");

            // Each call recorded exactly its own failure, under its own id, and its three events kept
            // their own order. A record that had leaked between two flows would show up as a second
            // tool.failed here or as one of these kinds out of place.
            var failed = Assert.Single(events, item => item.Kind == AuditEventKind.ToolFailed);
            Assert.Equal("lookup_order", failed.Payload[AuditPayloadKeys.ToolName]);
            Assert.NotEmpty(failed.Payload[AuditPayloadKeys.ToolCallId]);
            Assert.Equal(
                [AuditEventKind.CallStarted, AuditEventKind.ToolFailed, AuditEventKind.TurnCompleted],
                events.Select(item => item.Kind).ToArray());
            Assert.All(events, AuditEventVocabulary.Validate);
        }
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

        // Four facts, four identities, none of them repeated. What orders them is
        // audit_event.sequence, which the store assigns and which this test never sees.
        Assert.Equal(4, events.Count);
        Assert.Equal(events.Count, events.Select(item => item.EventId).Distinct().Count());

        // This is chain_check of section 11, item 6, run over what the turn loop produced.
        Assert.All(events, AuditEventVocabulary.Validate);
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
        Func<ToolConfiguration, AITool?>? tools = null,
        TimeProvider? timeProvider = null,
        IAuditSinkPort? auditSink = null,
        ILogger? logger = null)
    {
        // There is always a sink now: CallObservers.Standard takes a required one, because the
        // composition root resolves providers.audit for every host and falls back to the in-process
        // memory kind. An optional parameter has to be a compile-time constant, so the default is
        // spelled here instead — a fact that does not care where its events land gets a fresh
        // in-memory sink and reads exactly as it did when it passed nothing.
        IAuditSinkPort sink = auditSink ?? new InMemoryAuditSink();

        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(reply);
        if (fill is not null)
        {
            chatClients.Route("fill", fill);
        }

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
            });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            timeProvider,
            logger,
            CallObservers.Standard(sink, logger));
    }
}

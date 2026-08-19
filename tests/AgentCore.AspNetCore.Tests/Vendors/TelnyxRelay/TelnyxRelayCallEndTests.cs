using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;

/// <summary>
/// How the chain of one call closes when the socket ends.
/// </summary>
/// <remarks>
/// <para>
/// Section 11, item 6 and T55/T56: every call writes hash-chained events ending in
/// <c>call.ended</c>, and the reason is one member of the closed set
/// <see cref="CallEndReason"/> names. The turn loop closes its own chain when the stage machine
/// reaches a terminal stage, and that is the only ending the core can see. Every other ending is
/// the adapter's to write, because only the adapter sees the socket end — which is what
/// <see cref="CallSession.EndCall(CallEndReason)"/> says in its own remarks.
/// </para>
/// <para>
/// Every test here drives <c>TelnyxRelayConnection.RunAsync</c> over <see cref="FakeWebSocket"/>
/// rather than over a real port. The close of a call is exactly the moment a real socket stops
/// being observable — <see cref="FakeRelayClient"/> aborts its own socket on the way out, so a
/// graceful vendor close cannot be scripted over the wire at all — and the fake is what lets a test
/// script the vendor's own close frame, a faulting write loop, and a host that stops, each on its
/// own. Everything else about the connection is the real thing, including the session factory, the
/// store, the observers, and the audit queue <c>AddAgentCore</c> registers.
/// </para>
/// <para>
/// Every test here runs offline against a fake model. There is no Telnyx account, no network call,
/// and no API key anywhere in this file. That is T59.
/// </para>
/// </remarks>
public sealed class TelnyxRelayCallEndTests
{
    /// <summary>A document whose first turn moves the machine into a terminal stage.</summary>
    /// <remarks>
    /// The transition carries no guard, so one turn is enough to reach <c>close</c> and the turn
    /// loop closes the chain itself with <c>agent.completed</c>. That is the one ending teardown
    /// must not write over.
    /// </remarks>
    private const string TerminalStageYaml =
        """
        apiVersion: agentcore/v1
        name: relay-call-end
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller" }
            - { id: closer,  instructions: "close the call" }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: greeter, to: [ { stage: close } ] }
            - { id: close,    agent: closer,  terminal: true }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    [Fact(Timeout = 30_000)]
    public async Task ASocketTheRelayEnds_WritesOneCallEndedThatNamesTheCallerHangup()
    {
        // The ordinary end of a call. The read loop sees the vendor's own close frame, teardown
        // picks NormalClosure, and the chain has to close on the reason a report counts years
        // later — not stop mid-chain with no terminal event at all.
        using SequencedChatClient reply = new("hello");
        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply);

        // One channel, read in order, and DispatchAsync awaits StartCallAsync: the session exists
        // before the read loop ever sees the close behind it, so nothing here has to poll for it.
        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-hangup"));
        harness.Socket.QueueClose();

        await WaitForTeardownAsync(harness);

        var events = await ReadChainAsync(harness, "call-hangup");

        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.CallEnded],
            events.Select(item => item.Kind).ToArray());
        Assert.Equal("caller.hangup", EndReasonOf(events));
        Assert.True(AuditChain.Verify(AuditChain.LinkAll(events)).IsIntact);
    }

    [Fact(Timeout = 30_000)]
    public async Task AConnectionWhoseWriteLoopFaulted_WritesOneCallEndedThatNamesTheFault()
    {
        // Nothing on a healthy socket makes a send throw, so the fault is injected. The write loop
        // faults, teardown picks InternalServerError, and the ending recorded must be the fault
        // rather than a hang-up nobody performed.
        using SequencedChatClient reply = new("hello there caller");
        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply);

        harness.Socket.FailEverySend(new InvalidOperationException("the send failed."));
        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-write-faulted"));
        harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));

        await WaitForTeardownAsync(harness);

        var events = await ReadChainAsync(harness, "call-write-faulted");

        Assert.Equal(AuditEventKind.CallEnded, events[^1].Kind);
        Assert.Single(events, item => item.Kind == AuditEventKind.CallEnded);
        Assert.Equal("call.faulted", EndReasonOf(events));
        Assert.True(AuditChain.Verify(AuditChain.LinkAll(events)).IsIntact);
    }

    [Fact(Timeout = 30_000)]
    public async Task AHostThatStopsUnderALiveCall_WritesOneCallEndedThatNamesTheFault()
    {
        // The caller did not hang up: the process went away underneath them. The closed set of
        // section 4 holds four endings and this is not one of the other three, so the honest one is
        // the fault. Recording it as caller.hangup would have a report count a shutdown as a caller
        // choosing to leave.
        using SequencedChatClient reply = new("hello");
        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply);

        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-host-stopping"));
        await WaitForSessionAsync(harness, "call-host-stopping");
        harness.StopApplication();

        await WaitForTeardownAsync(harness);

        var events = await ReadChainAsync(harness, "call-host-stopping");

        Assert.Equal(AuditEventKind.CallEnded, events[^1].Kind);
        Assert.Equal("call.faulted", EndReasonOf(events));
        Assert.True(AuditChain.Verify(AuditChain.LinkAll(events)).IsIntact);
    }

    [Fact(Timeout = 30_000)]
    public async Task ACallThatAlreadyReachedItsTerminalStage_GetsNoSecondTerminalEvent()
    {
        // The turn loop closed this chain itself, with agent.completed and the stage that ended it.
        // EndCall is idempotent, and this is the proof that the idempotence really holds through
        // the adapter's path: one call.ended in the chain, and the reason is the agent's, not the
        // socket's.
        using SequencedChatClient reply = new("goodbye then");
        await using var harness = await RelayConnectionHarness.StartAsync(TerminalStageYaml, reply);

        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-completed"));
        harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));

        // The session's own completion flag, and never the last frame on the wire: the reply's
        // closing frame leaves before the turn loop commits the turn, so a test that closed the
        // socket on that frame would race the very event it is about to assert on.
        var session = await WaitForCompletedCallAsync(harness, "call-completed");
        Assert.True(session.IsComplete);

        harness.Socket.QueueClose();
        await WaitForTeardownAsync(harness);

        var events = await ReadChainAsync(harness, "call-completed");

        Assert.Single(events, item => item.Kind == AuditEventKind.CallEnded);
        Assert.Equal("agent.completed", EndReasonOf(events));
        Assert.True(AuditChain.Verify(AuditChain.LinkAll(events)).IsIntact);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASocketThatEndedBeforeTheSetupFrame_WritesNothingAndTearsDownCleanly()
    {
        // No setup frame ever arrived, so there is no call, no session, and nothing to close. A
        // chain with a call.ended and no call.started would be a record of a call that never
        // happened, and teardown must not throw its way out of the request handler either.
        using SequencedChatClient reply = new("hello");
        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply);

        harness.Socket.QueueClose();

        await WaitForTeardownAsync(harness);

        Assert.True(harness.Connection.IsCompletedSuccessfully);
        await Queue(harness).FlushAsync(TestContext.Current.CancellationToken);
        Assert.Empty(Sink(harness).Events);
    }

    [Fact(Timeout = 30_000)]
    public async Task AClockThatThrowsWhileTheChainCloses_StillTearsDownAndStillReleasesTheSession()
    {
        // Section 7.1: teardown never throws out of the request handler. The one input the closing
        // event reads that can throw is the clock, so it is the one a test can make throw. A throw
        // here must cost the chain its last event and nothing else — never the session removal
        // behind it, because InMemoryCallSessionStore evicts nothing on its own and a session left
        // there lives for the rest of the process.
        FaultingClock clock = new();
        using SequencedChatClient reply = new("hello");
        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            configure: options => options.TimeProvider = clock);

        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-broken-clock"));
        await WaitForSessionAsync(harness, "call-broken-clock");

        // Armed only once the session exists: the session reads the clock as it is built, and a
        // clock that failed that read would end the test before the path it is meant to reach.
        clock.FailFromNowOn();
        harness.Socket.QueueClose();

        await WaitForTeardownAsync(harness);

        Assert.True(harness.Connection.IsCompletedSuccessfully);
        Assert.Null(await Store(harness).TryGetAsync("call-broken-clock", TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30_000)]
    public async Task EndCall_HangUp_FlushesTranscriptBeforeSessionRemoval()
    {
        // Arrange
        //
        // Store 1 is written off the turn, so a call can end with its last words still in flight.
        // The session is the only thing that can wait for them, so once it leaves the store nothing
        // can: the record of that call would lose the turn the caller just had, and no error would
        // say so. Same shape as the teardown that removed a session without closing its chain.
        ParkingCallMessageStore transcript = new();
        OrderedCallSessionStore sessions = new(() => transcript.Landed);
        using SequencedChatClient reply = new("your order ships Friday");
        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            relay: options => options.CloseTimeout = TimeSpan.FromSeconds(30),
            services: collection =>
            {
                collection.AddSingleton<ICallSessionStore>(sessions);
                collection.AddSingleton<ICallSessionFactory>(
                    TranscriptBackedSessions(TelnyxRelayTurnTests.PolicyYaml, reply, transcript));
            });

        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-flush"));
        harness.Socket.Queue(RelayFrames.Prompt("when does my order ship?", last: true));
        await transcript.Parked.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // Act
        harness.Socket.QueueClose();

        // The write is still parked, so a teardown that waits for it cannot reach the removal and
        // this wait runs out. A teardown that does not wait reaches the removal at once and the
        // wait ends early — which is the failure this test exists to catch, and why the release
        // below comes after the wait rather than before it.
        await WaitQuietlyAsync(sessions.Removing, TimeSpan.FromSeconds(2));
        transcript.Release();
        await WaitForTeardownAsync(harness);

        // Assert
        Assert.True(
            sessions.TranscriptLandedAtRemoval,
            "the session was dropped while store 1 still owed the call its last turn.");
        Assert.Null(await sessions.TryGetAsync("call-flush", TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30_000)]
    public async Task StartCall_SecondSetupFrame_FlushesTheReplacedTranscriptBeforeDroppingIt()
    {
        // Arrange
        //
        // A second setup frame replaces the session rather than refusing the socket, and the first
        // one is dropped there and then. It is dropped by the same rule teardown obeys: the session
        // is the only thing that can wait for the words it queued, so the first call's record would
        // lose its last turn.
        ParkingCallMessageStore transcript = new();
        OrderedCallSessionStore sessions = new(() => transcript.Landed);
        using SequencedChatClient reply = new("your order ships Friday");
        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            relay: options => options.CloseTimeout = TimeSpan.FromSeconds(30),
            services: collection =>
            {
                collection.AddSingleton<ICallSessionStore>(sessions);
                collection.AddSingleton<ICallSessionFactory>(
                    TranscriptBackedSessions(TelnyxRelayTurnTests.PolicyYaml, reply, transcript));
            });

        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-first"));
        harness.Socket.Queue(RelayFrames.Prompt("when does my order ship?", last: true));
        await transcript.Parked.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // Act
        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-second"));

        // Read as in EndCall_HangUp_FlushesTranscriptBeforeSessionRemoval: the wait runs out while
        // the write is parked, and ends early when the drop did not wait for it.
        await WaitQuietlyAsync(sessions.Removing, TimeSpan.FromSeconds(2));
        transcript.Release();

        // Assert
        await WaitForSessionAsync(harness, "call-second");
        Assert.True(
            sessions.TranscriptLandedAtRemoval,
            "the replaced session was dropped while store 1 still owed its call the last turn.");
    }

    /// <summary>Builds the session factory of one document over a given store 1.</summary>
    /// <param name="yaml">The document, as YAML.</param>
    /// <param name="reply">The model behind every agent.</param>
    /// <param name="transcript">Where the words of a call are written.</param>
    /// <returns>The factory, ready to register over the one <c>AddAgentCore</c> built.</returns>
    /// <remarks>
    /// The composition root binds no store 1 yet, so every host compiles onto the memory one. This
    /// is the only seam a test has for putting a slow store behind a call, and it is why the whole
    /// factory is rebuilt rather than decorated: the store is compiled into the agent, and the
    /// factory holds the compiled agent.
    /// </remarks>
    private static CallSessionFactory TranscriptBackedSessions(
        string yaml, IChatClient reply, ICallMessageStore transcript)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(reply);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { MessageStore = transcript });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients));
    }

    /// <summary>Waits for one task, and treats running out of time as an answer rather than a fault.</summary>
    /// <param name="task">What to wait for.</param>
    /// <param name="bound">How long to give it.</param>
    /// <returns>A task that completes either way.</returns>
    private static async Task WaitQuietlyAsync(Task task, TimeSpan bound)
    {
        try
        {
            await task.WaitAsync(bound, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            // The wait running out is what this caller is asking about.
        }
    }

    /// <summary>Waits for one connection to finish its own teardown.</summary>
    /// <param name="harness">The running connection.</param>
    /// <returns>A task that completes once teardown has run to its end.</returns>
    /// <remarks>
    /// The connection task is awaited through a guard rather than directly, so a teardown that
    /// throws — which section 7.1 forbids — fails here by name instead of surfacing as whatever
    /// assertion happened to run next.
    /// </remarks>
    private static async Task WaitForTeardownAsync(RelayConnectionHarness harness)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await harness.Connection.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never tore down within twenty seconds.");
        }
        catch (Exception fault)
        {
            Assert.Fail($"teardown threw out of the request handler, which section 7.1 forbids: {fault}");
        }
    }

    /// <summary>Waits until the store holds one call.</summary>
    /// <param name="harness">The running connection.</param>
    /// <param name="callId">The id of the call.</param>
    /// <returns>A task that completes once the session appears.</returns>
    private static async Task WaitForSessionAsync(RelayConnectionHarness harness, string callId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await Store(harness).TryGetAsync(callId, TestContext.Current.CancellationToken) is not null)
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"the session of call '{callId}' never appeared.");
    }

    /// <summary>Waits until one call has closed its own chain from the terminal stage.</summary>
    /// <param name="harness">The running connection.</param>
    /// <param name="callId">The id of the call.</param>
    /// <returns>The completed session.</returns>
    private static async Task<CallSession> WaitForCompletedCallAsync(RelayConnectionHarness harness, string callId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (await Store(harness).TryGetAsync(callId, TestContext.Current.CancellationToken)
                is { IsComplete: true } session)
            {
                return session;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"the call '{callId}' never reached its terminal stage.");
        throw new InvalidOperationException("unreachable: Assert.Fail always throws.");
    }

    /// <summary>Flushes the queue and reads back the chain of one call.</summary>
    /// <param name="harness">The connection that has already torn down.</param>
    /// <param name="callId">The id of the call.</param>
    /// <returns>The events of that call, oldest first.</returns>
    /// <remarks>
    /// The queue is what keeps the append off the turn, so a reader that wants the rows now asks
    /// for them now. Nothing here waits on the chain being non-empty: every test that calls this
    /// has already waited for the teardown that writes the last event.
    /// </remarks>
    private static async Task<IReadOnlyList<AuditEvent>> ReadChainAsync(
        RelayConnectionHarness harness,
        string callId)
    {
        await Queue(harness).FlushAsync(TestContext.Current.CancellationToken);
        return Sink(harness).EventsOf(callId);
    }

    /// <summary>Reads the end reason of the last event of one chain.</summary>
    /// <param name="events">The chain.</param>
    /// <returns>The wire token under <see cref="AuditPayloadKeys.EndReason"/>.</returns>
    private static string EndReasonOf(IReadOnlyList<AuditEvent> events)
    {
        var ended = Assert.Single(events, item => item.Kind == AuditEventKind.CallEnded);
        return ended.Payload[AuditPayloadKeys.EndReason];
    }

    /// <summary>Reads back the queue the composition root put in front of the store.</summary>
    private static QueuedAuditSink Queue(RelayConnectionHarness harness)
        => Assert.IsType<QueuedAuditSink>(harness.Services.GetRequiredService<Application.Ports.IAuditSinkPort>());

    /// <summary>Reads back the store the chain lands in.</summary>
    private static InMemoryAuditSink Sink(RelayConnectionHarness harness)
        => harness.Services.GetRequiredService<InMemoryAuditSink>();

    /// <summary>Reads back the live session store.</summary>
    private static ICallSessionStore Store(RelayConnectionHarness harness)
        => harness.Services.GetRequiredService<ICallSessionStore>();
}

/// <summary>
/// A clock a test breaks on demand.
/// </summary>
/// <remarks>
/// <see cref="TimeProvider.GetUtcNow"/> is the one call <c>CallSession.EndCall</c> makes that can
/// throw at all: the reason is a member of a closed set the connection picks itself, and the
/// dispatcher behind the event swallows everything an observer raises. Breaking the clock is
/// therefore the only way a test can reach the guard that keeps section 7.1's promise — teardown
/// never throws out of the request handler.
/// </remarks>
internal sealed class FaultingClock : TimeProvider
{
    private volatile bool _failing;

    /// <summary>Makes every later reading of the wall clock throw.</summary>
    public void FailFromNowOn() => _failing = true;

    /// <inheritdoc />
    /// <remarks>
    /// Only this reading fails. The timestamps a turn measures with and the timers the pump's idle
    /// deadline runs on are left alone, so a broken clock ends nothing but the one event under test.
    /// </remarks>
    public override DateTimeOffset GetUtcNow()
        => _failing
            ? throw new InvalidOperationException("the clock failed.")
            : base.GetUtcNow();
}

/// <summary>
/// A store 1 that holds its first write open until a test releases it.
/// </summary>
/// <remarks>
/// A real store talks to a database, so a write outlives the turn that queued it. The memory store
/// every other test runs on lands a write before the next line reads it, and would let teardown drop
/// a session with a write still owing without any test noticing.
/// </remarks>
internal sealed class ParkingCallMessageStore : ICallMessageStore
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _landed;

    /// <summary>Gets a task that completes once the first append is parked.</summary>
    public Task Parked => _entered.Task;

    /// <summary>Gets whether the parked append has finished writing.</summary>
    public bool Landed => _landed;

    /// <summary>Lets the parked append finish.</summary>
    public void Release() => _release.TrySetResult();

    /// <inheritdoc />
    public async ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        _landed = true;
    }

    /// <inheritdoc />
    public ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

/// <summary>
/// The live session store, with a note of what store 1 had done by the time a session was dropped.
/// </summary>
internal sealed class OrderedCallSessionStore(Func<bool> transcriptLanded) : ICallSessionStore
{
    private readonly InMemoryCallSessionStore _inner = new();
    private readonly TaskCompletionSource _removing = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets a task that completes when a session is first dropped.</summary>
    public Task Removing => _removing.Task;

    /// <summary>Gets whether store 1 had written the call's words when the session was dropped.</summary>
    public bool? TranscriptLandedAtRemoval { get; private set; }

    /// <inheritdoc />
    public ValueTask<CallSession?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.TryGetAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public ValueTask AddAsync(CallSession session, CancellationToken cancellationToken = default)
        => _inner.AddAsync(session, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        TranscriptLandedAtRemoval ??= transcriptLanded();
        _removing.TrySetResult();
        return _inner.RemoveAsync(sessionId, cancellationToken);
    }
}

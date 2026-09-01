using System.Text.Json.Nodes;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// A call outlives the session that started it.
/// </summary>
/// <remarks>
/// A session is held in memory and a call is not: the host restarts, the session expires, and the
/// same call arrives again on a new one. Until this file, that second session started at zero on
/// every counter it owns — an empty history, ordinal 0, turn 0 — so the caller met an agent with no
/// memory of them, and its first append collided with the rows the first session had already
/// written. Store 1 is what the second session is missing, and store 1 is where it is read from.
/// </remarks>
public sealed class CallSessionResumeTests
{
    private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        name: resume-check
        agents:
          items:
            - { id: only, instructions: "greet the caller" }
        """;

    /// <summary>
    /// A document with something to forget: one slot a writer fills, and a stage that moves.
    /// </summary>
    /// <remarks>
    /// The slot is a counter and the guard reads a reserved slot, so one plain turn fills both with
    /// no extractor and no tool. That is the point: the test is about what survives a second
    /// session, not about how the value got there.
    /// </remarks>
    private const string StagedYaml = """
        apiVersion: agentcore/v1
        name: resume-state-check
        state:
          turnsTaken:
            type: integer
            default: 0
            writer: counter
            increment: { ">=": [ { var: turnIndex }, 0 ] }
        guards:
          pastFirstTurn: { ">": [ { var: turnIndex }, 0 ] }
        agents:
          items:
            - { id: intake, instructions: "ask the caller for the model" }
            - { id: help,   instructions: "help the caller" }
        policy:
          initial: intake
          stages:
            - { id: intake, agent: intake, to: [ { stage: help, when: pastFirstTurn } ] }
            - { id: help,   agent: help }
        """;

    /// <summary>
    /// A document whose stages run three deep, so a machine left in the first one is visible.
    /// </summary>
    /// <remarks>
    /// Two stages cannot show it. Restoring the second one and then taking a turn lands on the
    /// third only if the machine itself moved; a machine still holding the first stage walks to the
    /// second and reports the same answer a restored one would have started from.
    /// </remarks>
    private const string ThreeStageYaml = """
        apiVersion: agentcore/v1
        name: resume-machine-check
        guards:
          always: { ">=": [ { var: turnIndex }, 0 ] }
        agents:
          items:
            - { id: first,  instructions: "greet the caller" }
            - { id: second, instructions: "ask the caller for the model" }
            - { id: third,  instructions: "help the caller" }
        policy:
          initial: one
          stages:
            - { id: one,   agent: first,  to: [ { stage: two,   when: always } ] }
            - { id: two,   agent: second, to: [ { stage: three, when: always } ] }
            - { id: three, agent: third }
        """;

    [Fact]
    public async Task ASecondSessionOfOneCall_SendsTheFirstTurnToTheModel()
    {
        InMemoryCallStore store = new();
        var callId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

        using ScriptedChatClient reply = new("Dana");
        using RequestCapturingChatClient capture = new(reply);
        var resumed = CreateSession(OneAgentYaml, capture, store, callId);

        await resumed.RunTurnAsync("what is my name?", TestContext.Current.CancellationToken);

        Assert.NotEmpty(capture.Requests);
        Assert.Contains(
            capture.Requests[^1],
            message => message.Text.Contains("my name is Dana", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_KeepsEveryWordTheFirstOneWrote()
    {
        InMemoryCallStore store = new();
        var callId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

        await SecondTurnAsync(store, callId, "what is my name?", "Dana");

        var rows = await store.ReadAsync(callId, TestContext.Current.CancellationToken);

        // Two turns, each writing what the caller said and what it heard. A resumed session that
        // restarted its ordinals would overwrite the first pair rather than follow them.
        Assert.Equal(4, rows.Count);
        Assert.Equal([0, 1, 2, 3], rows.Select(row => row.Ordinal));
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_CountsItsTurnAsTheNextOne()
    {
        InMemoryCallStore store = new();
        var callId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

        await SecondTurnAsync(store, callId, "what is my name?", "Dana");

        var rows = await store.ReadAsync(callId, TestContext.Current.CancellationToken);

        // The turn index is the join to the audit chain, so a second turn numbered 0 would claim the
        // first turn's events as its own.
        Assert.Equal([0, 0, 1, 1], rows.Select(row => row.TurnIndex));
    }

    [Fact]
    public async Task ASessionOfACallWithNoWords_StartsAtTheBeginning()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("empty-call", TestContext.Current.CancellationToken);

        await SecondTurnAsync(store, "empty-call", "hello", "hi there");

        var rows = await store.ReadAsync("empty-call", TestContext.Current.CancellationToken);

        Assert.Equal([0, 1], rows.Select(row => row.Ordinal));
        Assert.Equal([0, 0], rows.Select(row => row.TurnIndex));
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_WhoseWordsCannotBeRead_RefusesTheTurn()
    {
        InMemoryCallStore backing = new();
        UnreadableCallStore store = new(backing);
        var callId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

        using ScriptedChatClient reply = new("Dana");
        var resumed = CreateSession(OneAgentYaml, reply, store, callId);

        // A read that answered empty would run the turn with no memory of the caller. Meeting a
        // stranger is worse than meeting an error, so the turn does not run at all.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resumed.RunTurnAsync("what is my name?", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_KeepsTheStageTheFirstOneReached()
    {
        InMemoryCallStore store = new();

        using ScriptedChatClient first = new("what model is it?");
        var opened = CreateSession(StagedYaml, first, store);
        await opened.RunTurnAsync("hello", TestContext.Current.CancellationToken);
        await opened.FlushTranscriptAsync();

        Assert.Equal("help", opened.Stage);

        using ScriptedChatClient second = new("sure");
        var resumed = CreateSession(StagedYaml, second, store, opened.CallId);
        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        // StageBefore, and not Stage. The stage is restored when the session OPENS and the session
        // opens on the first turn, so the restored stage is the one that turn spoke in, and 'intake'
        // is what a session that forgot would report. Reading Stage after the turn would prove
        // nothing: this document's only guard reads the reserved turnIndex slot, which store 1
        // restores off the words on its own, so even a session that remembered nothing would land in
        // 'help' by the end of the turn.
        Assert.Equal("help", resumed.LastTurn!.StageBefore);
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_MovesTheStageMachineAndNotOnlyItsLabel()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("C-machine", TestContext.Current.CancellationToken);

        await store.AppendAsync(
            [new CallMessage("C-machine", 0, 0, new ChatMessage(ChatRole.User, "hello"))],
            new CallSessionState { Stage = "two" },
            TestContext.Current.CancellationToken);

        using ScriptedChatClient reply = new("sure");
        var resumed = CreateSession(ThreeStageYaml, reply, store, "C-machine");

        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        // 'three', because the turn ran in 'two' and moved on from there. A session that restored
        // the reserved stage slot and left the machine where the document starts would report 'two'
        // twice over: 'two' as the stage the turn spoke in, because the slot says so, and 'two'
        // again afterwards, because the machine only just walked to it — while the turn itself was
        // answered by the agent of 'one'.
        Assert.Equal("two", resumed.LastTurn!.StageBefore);
        Assert.Equal("three", resumed.Stage);
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_KeepsTheSlotsTheFirstOneFilled()
    {
        InMemoryCallStore store = new();

        using ScriptedChatClient first = new("what model is it?");
        var opened = CreateSession(StagedYaml, first, store);
        await opened.RunTurnAsync("hello", TestContext.Current.CancellationToken);
        await opened.FlushTranscriptAsync();

        Assert.Equal(1L, opened.State.Read("turnsTaken")!.GetValue<long>());

        using ScriptedChatClient second = new("sure");
        var resumed = CreateSession(StagedYaml, second, store, opened.CallId);
        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        // Two, not one. A session that forgot would count its own turn from zero.
        Assert.Equal(2L, resumed.State.Read("turnsTaken")!.GetValue<long>());
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_DropsASlotTheDocumentNoLongerDeclares()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("C-drift", TestContext.Current.CancellationToken);

        // Written as though a build that declared 'retired' had run this call.
        await store.AppendAsync(
            [new CallMessage("C-drift", 0, 0, new ChatMessage(ChatRole.User, "hello"))],
            new CallSessionState
            {
                Stage = "intake",
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["turnsTaken"] = JsonValue.Create(4L),
                    ["retired"] = JsonValue.Create("gone"),
                },
            },
            TestContext.Current.CancellationToken);

        using ScriptedChatClient reply = new("sure");
        RecordingObserver observer = new();
        var resumed = CreateSession(StagedYaml, reply, store, "C-drift", observer);
        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        Assert.Equal(5L, resumed.State.Read("turnsTaken")!.GetValue<long>());
        Assert.Null(resumed.State.Read("retired"));

        // Dropped, and said so. A document change that quietly costs a call one slot is a change
        // nobody can price afterwards, so the slot that went is named in the fact.
        var dropped = Assert.Single(observer.Events, fact => fact.Kind == CallEventKind.StateRestorePartial);
        Assert.Equal(
            "the document no longer declares the slot 'retired'.",
            dropped.Payload[CallEventPayloadKeys.Reason]);
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_DropsASlotWhoseDeclaredTypeNoLongerTakesTheStoredValue()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("C-retyped", TestContext.Current.CancellationToken);

        // Written as though 'turnsTaken' had been declared a string when this call ran.
        await store.AppendAsync(
            [new CallMessage("C-retyped", 0, 0, new ChatMessage(ChatRole.User, "hello"))],
            new CallSessionState
            {
                Stage = "intake",
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["turnsTaken"] = JsonValue.Create("four"),
                },
            },
            TestContext.Current.CancellationToken);

        using ScriptedChatClient reply = new("sure");
        RecordingObserver observer = new();
        var resumed = CreateSession(StagedYaml, reply, store, "C-retyped", observer);
        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        // Back to the declared default, and then this turn's own increment.
        Assert.Equal(1L, resumed.State.Read("turnsTaken")!.GetValue<long>());

        // The other half of what TryWrite answers false for. An operator fixes a retyped slot and a
        // deleted slot differently, so the two reasons have to read differently.
        var dropped = Assert.Single(observer.Events, fact => fact.Kind == CallEventKind.StateRestorePartial);
        Assert.Equal(
            "the slot 'turnsTaken' no longer takes the value it was stored with.",
            dropped.Payload[CallEventPayloadKeys.Reason]);
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_DropsAReservedSlotRatherThanRefusingTheCall()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("C-reserved", TestContext.Current.CancellationToken);

        // No writer produces this, but the blob is arbitrary JSON out of store 0 and a host can hand
        // one in directly. StateDocument.TryWrite THROWS on a reserved slot rather than answering
        // false, so a Restore that just asked it would take the call down on its first turn.
        await store.AppendAsync(
            [new CallMessage("C-reserved", 0, 0, new ChatMessage(ChatRole.User, "hello"))],
            new CallSessionState
            {
                Stage = "intake",
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["turnsTaken"] = JsonValue.Create(4L),
                    ["stage"] = JsonValue.Create("help"),
                },
            },
            TestContext.Current.CancellationToken);

        using ScriptedChatClient reply = new("sure");
        RecordingObserver observer = new();
        var resumed = CreateSession(StagedYaml, reply, store, "C-reserved", observer);

        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        // It ran, and the declared slot beside the reserved one still landed.
        Assert.NotNull(resumed.LastTurn);
        Assert.Equal(5L, resumed.State.Read("turnsTaken")!.GetValue<long>());

        var dropped = Assert.Single(observer.Events, fact => fact.Kind == CallEventKind.StateRestorePartial);
        Assert.Equal(
            "the slot 'stage' is reserved, and a reserved slot is never restored.",
            dropped.Payload[CallEventPayloadKeys.Reason]);
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_IgnoresAStateBlobWrittenInAShapeThisBuildDoesNotKnow()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("C-future", TestContext.Current.CancellationToken);

        await store.AppendAsync(
            [new CallMessage("C-future", 0, 0, new ChatMessage(ChatRole.User, "hello"))],
            new CallSessionState
            {
                Version = CallSessionState.CurrentVersion + 1,
                Stage = "help",
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["turnsTaken"] = JsonValue.Create(4L),
                },
            },
            TestContext.Current.CancellationToken);

        using ScriptedChatClient reply = new("sure");
        RecordingObserver observer = new();
        var resumed = CreateSession(StagedYaml, reply, store, "C-future", observer);

        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        // Nothing of it is taken, not the stage and not the slot. A call that forgets answers the
        // caller; a call that guesses at a shape it does not know answers them wrongly.
        Assert.Equal("intake", resumed.LastTurn!.StageBefore);
        Assert.Equal(1L, resumed.State.Read("turnsTaken")!.GetValue<long>());

        var dropped = Assert.Single(observer.Events, fact => fact.Kind == CallEventKind.StateRestorePartial);
        Assert.Equal(
            $"the stored state is version {CallSessionState.CurrentVersion + 1} "
                + $"and this build writes {CallSessionState.CurrentVersion}.",
            dropped.Payload[CallEventPayloadKeys.Reason]);
    }

    [Fact]
    public async Task ASecondSessionOfACallWhoseDocumentLostItsPolicy_DropsTheStoredStage()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("C-nopolicy", TestContext.Current.CancellationToken);

        await store.AppendAsync(
            [new CallMessage("C-nopolicy", 0, 0, new ChatMessage(ChatRole.User, "hello"))],
            new CallSessionState { Stage = "intake" },
            TestContext.Current.CancellationToken);

        using ScriptedChatClient reply = new("sure");
        RecordingObserver observer = new();

        // OneAgentYaml declares no policy: at all, which is the shape a document takes when its
        // policy section is removed between one session of a call and the next.
        var resumed = CreateSession(OneAgentYaml, reply, store, "C-nopolicy", observer);

        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        // Empty, not 'intake'. There is no machine to hold that stage, and writing it into the
        // reserved slot anyway would hand the guards and the audit chain a stage nothing is in.
        Assert.Equal(string.Empty, resumed.Stage);

        var dropped = Assert.Single(observer.Events, fact => fact.Kind == CallEventKind.StateRestorePartial);
        Assert.Equal(
            "the document declares no policy, so the stage 'intake' has nowhere to go.",
            dropped.Payload[CallEventPayloadKeys.Reason]);
    }

    [Fact]
    public async Task ASecondSessionOfACallThatAlreadyFinished_RefusesTheTurn()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("C-finished", TestContext.Current.CancellationToken);

        await store.AppendAsync(
            [new CallMessage("C-finished", 0, 0, new ChatMessage(ChatRole.User, "hello"))],
            new CallSessionState { Stage = "help", IsComplete = true },
            TestContext.Current.CancellationToken);

        using ScriptedChatClient reply = new("sure");
        var resumed = CreateSession(StagedYaml, reply, store, "C-finished");

        // The restore happens in OpenSessionAsync, which RunTurnAsync awaits before it reads
        // IsComplete — so a call that reached a terminal stage stays finished across sessions and
        // turns the caller away rather than starting again. Worth pinning rather than inferring: it
        // is what a chat page reload on a finished call now does.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_FallsBackWhenTheStageIsGone()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("C-nostage", TestContext.Current.CancellationToken);

        await store.AppendAsync(
            [new CallMessage("C-nostage", 0, 0, new ChatMessage(ChatRole.User, "hello"))],
            new CallSessionState { Stage = "a-stage-nobody-declares" },
            TestContext.Current.CancellationToken);

        using ScriptedChatClient reply = new("sure");
        RecordingObserver observer = new();
        var resumed = CreateSession(StagedYaml, reply, store, "C-nostage", observer);

        await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

        // It ran. Falling back beats refusing: a call that comes back knowing less is worth more than
        // one that will not come back.
        Assert.NotNull(resumed.LastTurn);
        Assert.Equal("intake", resumed.LastTurn!.StageBefore);

        var dropped = Assert.Single(observer.Events, fact => fact.Kind == CallEventKind.StateRestorePartial);
        Assert.Contains(
            "a-stage-nobody-declares", dropped.Payload[CallEventPayloadKeys.Reason], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallWithNoStoredState_StartsWhereTheDocumentSays()
    {
        InMemoryCallStore store = new();

        using ScriptedChatClient reply = new("what model is it?");
        var session = CreateSession(StagedYaml, reply, store);

        await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        Assert.NotNull(session.LastTurn);
        Assert.Equal("intake", session.LastTurn!.StageBefore);
    }

    [Fact]
    public async Task ATurnWhoseWriteQueuesBehindASlowOne_StoresItsOwnStateAndNotALaterTurns()
    {
        ParkingRecordingCallStore store = new(new InMemoryCallStore());

        using ScriptedChatClient reply = new("sure");
        var session = CreateSession(StagedYaml, reply, store);

        // Three turns and no drain between them. The first write parks, so the second and the third
        // queue behind it exactly as they would behind a database that is still thinking.
        await session.RunTurnAsync("one", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("two", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("three", TestContext.Current.CancellationToken);

        store.Release();
        await session.FlushTranscriptAsync();

        var counted = store.States.Select(state => state!.Slots["turnsTaken"]!.GetValue<long>()).ToArray();

        // One, two, three: each write carries the state of the turn that queued it. A snapshot read
        // when the write finally ran would instead read whatever the call had reached by then, and
        // stamp the third turn's count on the two rows before it.
        Assert.Equal([1L, 2L, 3L], counted);
    }

    /// <summary>A store 1 that holds its first write open, and remembers the state each write carried.</summary>
    private sealed class ParkingRecordingCallStore(ICallStore inner) : DelegatingCallStore(inner)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly List<CallSessionState?> _states = [];
        private int _appends;

        /// <summary>Gets the state each append carried, in the order the store took them.</summary>
        public IReadOnlyList<CallSessionState?> States
        {
            get
            {
                lock (_gate)
                {
                    return [.. _states];
                }
            }
        }

        /// <summary>Lets the parked first write finish, and everything queued behind it with it.</summary>
        public void Release() => _release.TrySetResult();

        public override async ValueTask AppendAsync(
            IReadOnlyList<CallMessage> messages,
            CallSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _appends) == 1)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _states.Add(state);
            }

            await base.AppendAsync(messages, state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A store 1 that takes words and will not give them back.</summary>
    private sealed class UnreadableCallStore(ICallStore inner) : DelegatingCallStore(inner)
    {
        public override ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
            string callId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store 1 will not answer a read.");
    }

    private static async Task<string> FirstTurnAsync(
        ICallStore store, string said, string heard)
    {
        using ScriptedChatClient reply = new(heard);
        var session = CreateSession(OneAgentYaml, reply, store);

        await session.RunTurnAsync(said, TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        return session.CallId;
    }

    private static async Task SecondTurnAsync(
        ICallStore store, string callId, string said, string heard)
    {
        using ScriptedChatClient reply = new(heard);
        var session = CreateSession(OneAgentYaml, reply, store, callId);

        await session.RunTurnAsync(said, TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();
    }

    private static CallSession CreateSession(
        string yaml,
        IChatClient reply,
        ICallStore store,
        string? callId = null,
        ICallObserver? observer = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new FakeChatClientFactory(reply);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                CallStore = store,
                Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
            });

        var factory = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null,
            observers: observer is null ? null : [observer]);

        return factory.Create(callId);
    }

    /// <summary>Keeps every fact of the call, in the order the turn loop raised them.</summary>
    private sealed class RecordingObserver : ICallObserver
    {
        private readonly Lock _gate = new();
        private readonly List<CallEvent> _events = [];

        /// <summary>Gets what the call raised, oldest first.</summary>
        public IReadOnlyList<CallEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _events.Add(callEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}

using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Sessions;

/// <summary>
/// The lifecycle of one call's session: opened, found again, and closed.
/// </summary>
public sealed class CallSessionsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASessionThatWasOpenedIsFoundAgainUnderItsCallId()
    {
        InMemoryCallSessions sessions = new(Factory(), TimeSpan.FromMinutes(30), Clock());

        var opened = await sessions.OpenAsync("call-1", Token);

        Assert.Same(opened, await sessions.TryGetAsync("call-1", Token));
    }

    [Fact]
    public async Task ClosingACallWaitsForTheWordsItStillOwes()
    {
        // A real store answers over a network, so a write outlives the turn that queued it. This
        // session is the only thing that can wait for it, and closing is the last moment anything can.
        ParkingTranscriptStore transcript = new();
        InMemoryCallSessions sessions = new(Factory(transcript), TimeSpan.FromMinutes(30), Clock());
        var session = await sessions.OpenAsync("call-1", Token);

        var turn = session.RunTurnAsync("hello", Token);
        await transcript.Parked;

        var closing = sessions.CloseAsync("call-1", Token).AsTask();
        var returnedEarly = await Task.WhenAny(closing, Task.Delay(200, Token)) == closing;
        Assert.False(returnedEarly);

        transcript.Release();
        await closing;

        Assert.True(transcript.Landed);
        Assert.Null(await sessions.TryGetAsync("call-1", Token));
        await turn;
    }

    [Fact]
    public async Task ASessionNobodyTouchedPastTheIdleTimeoutIsClosed()
    {
        var clock = Clock();
        InMemoryCallSessions sessions = new(Factory(), TimeSpan.FromMinutes(30), clock);
        await sessions.OpenAsync("call-1", Token);

        clock.Advance(TimeSpan.FromMinutes(31));
        await sessions.SweepAsync(Token);

        // A caller that abandons a text call never reaches a terminal stage, so nothing else would
        // ever drop this session and the process would hold it for its whole life.
        Assert.Null(await sessions.TryGetAsync("call-1", Token));
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public async Task ReadingASessionPutsItsIdleClockBackToZero()
    {
        var clock = Clock();
        InMemoryCallSessions sessions = new(Factory(), TimeSpan.FromMinutes(30), clock);
        await sessions.OpenAsync("call-1", Token);

        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(await sessions.TryGetAsync("call-1", Token));

        // Forty minutes since it was opened, twenty since it was last read. A call still being had
        // must not be dropped out from under the caller.
        clock.Advance(TimeSpan.FromMinutes(20));
        await sessions.SweepAsync(Token);

        Assert.NotNull(await sessions.TryGetAsync("call-1", Token));
    }

    [Fact]
    public async Task AnExpiringSessionStillHandsOverTheWordsItOwed()
    {
        // The reason expiry goes through CloseAsync. An evictor that dropped the entry on its own
        // would return with the write still in flight, and nothing left able to wait for it.
        ParkingTranscriptStore transcript = new();
        var clock = Clock();
        InMemoryCallSessions sessions = new(Factory(transcript), TimeSpan.FromMinutes(30), clock);
        var session = await sessions.OpenAsync("call-1", Token);

        var turn = session.RunTurnAsync("hello", Token);
        await transcript.Parked;
        clock.Advance(TimeSpan.FromMinutes(31));

        var sweeping = sessions.SweepAsync(Token).AsTask();
        var returnedEarly = await Task.WhenAny(sweeping, Task.Delay(200, Token)) == sweeping;
        Assert.False(returnedEarly);

        transcript.Release();
        await sweeping;

        Assert.True(transcript.Landed);
        Assert.Equal(0, sessions.Count);
        await turn;
    }

    [Fact]
    public async Task AnExpiringSessionWritesTheLastEventOfItsChain()
    {
        // §11 item 6 makes call.ended the last event of every call. A caller who simply stops
        // replying closes no socket and reaches no terminal stage, so expiry is the only thing left
        // that can write it, and a chain with no end is a permanent gap in the record of D23.
        RecordingCallObserver observer = new();
        var clock = Clock();
        InMemoryCallSessions sessions = new(
            Factory(observer: observer), TimeSpan.FromMinutes(30), clock);
        await sessions.OpenAsync("call-1", Token);

        clock.Advance(TimeSpan.FromMinutes(31));
        await sessions.SweepAsync(Token);

        Assert.Contains(observer.Kinds, kind => kind == CallEventKind.CallEnded);
    }

    [Fact(Timeout = 30_000)]
    public async Task ATurnTellsTheStoreItsCallIsStillBeingHad()
    {
        // The relay opens its session once and holds it for the whole call, so nothing else reads
        // it back. A read is the one sign a store gets that a call is still live, so without one
        // the idle sweep drops a long call out from under the turn about to run.
        using SequencedChatClient reply = new("your order ships Friday");
        var factory = Factory(yaml: TelnyxRelayTurnTests.PolicyYaml, reply: reply);
        CountingCallSessions sessions = new(factory);

        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            services: collection =>
            {
                collection.AddSingleton<ICallSessions>(sessions);
                collection.AddSingleton<ICallSessionFactory>(factory);
            });

        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-live"));
        harness.Socket.Queue(RelayFrames.Prompt("when does my order ship?", last: true));

        for (var attempt = 0; attempt < 400 && sessions.Reads == 0; attempt++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(sessions.Reads > 0, "the turn never told the store its call is still being had.");
    }

    private static FakeTimeProvider Clock()
        => new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    private const string Document = """
        apiVersion: agentcore/v1
        name: call-sessions-tests
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    private static CallSessionFactory Factory(
        ITranscriptStore? transcript = null,
        string? yaml = null,
        IChatClient? reply = null,
        ICallObserver? observer = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml ?? Document);
        RoutingChatClientFactory chatClients = new(reply ?? new StubChatClient());
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { TranscriptStore = transcript });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            observers: observer is null ? null : [observer]);
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Records the kind of every event one call raised.</summary>
    private sealed class RecordingCallObserver : ICallObserver
    {
        private readonly List<CallEventKind> _kinds = [];

        public IReadOnlyList<CallEventKind> Kinds
        {
            get
            {
                lock (_kinds)
                {
                    return [.. _kinds];
                }
            }
        }

        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken = default)
        {
            lock (_kinds)
            {
                _kinds.Add(callEvent.Kind);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The default store, counting the reads that keep a call out of the idle sweep.</summary>
    private sealed class CountingCallSessions(ICallSessionFactory factory) : ICallSessions
    {
        private readonly InMemoryCallSessions _inner =
            new(factory, InMemoryCallSessions.DefaultIdleTimeout, TimeProvider.System);

        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public ValueTask<CallSession> OpenAsync(string? callId, CancellationToken cancellationToken = default)
            => _inner.OpenAsync(callId, cancellationToken);

        public ValueTask<CallSession?> TryGetAsync(string callId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return _inner.TryGetAsync(callId, cancellationToken);
        }

        public ValueTask CloseAsync(string callId, CancellationToken cancellationToken = default)
            => _inner.CloseAsync(callId, cancellationToken);
    }
}

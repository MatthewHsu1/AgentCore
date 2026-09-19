using AgentCore.TestSupport;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.Application.Tests.Sessions;

/// <summary>
/// The lifecycle of one conversation's session: opened, found again, and closed.
/// </summary>
public sealed class ConversationSessionsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASessionThatWasOpenedIsFoundAgainUnderItsConversationId()
    {
        InMemoryConversationSessions sessions = new(Factory(), TimeSpan.FromMinutes(30), Clock());

        var opened = await sessions.OpenAsync("conversation-1", Token);

        Assert.Same(opened, await sessions.TryGetAsync("conversation-1", Token));
    }

    [Fact]
    public async Task ClosingAConversationWaitsForTheWordsItStillOwes()
    {
        // A real store answers over a network, so a write outlives the turn that queued it. This
        // session is the only thing that can wait for it, and closing is the last moment anything can.
        ParkingConversationStore transcript = new();
        InMemoryConversationSessions sessions = new(Factory(transcript), TimeSpan.FromMinutes(30), Clock());
        var session = await sessions.OpenAsync("conversation-1", Token);

        var turn = session.RunTurnAsync("hello", Token);
        await transcript.Parked;

        var closing = sessions.CloseAsync("conversation-1", Token).AsTask();
        var returnedEarly = await Task.WhenAny(closing, Task.Delay(200, Token)) == closing;
        Assert.False(returnedEarly);

        transcript.Release();
        await closing;

        Assert.True(transcript.Landed);
        Assert.Null(await sessions.TryGetAsync("conversation-1", Token));
        await turn;
    }

    [Fact]
    public async Task ASessionNobodyTouchedPastTheIdleTimeoutIsClosed()
    {
        var clock = Clock();
        InMemoryConversationSessions sessions = new(Factory(), TimeSpan.FromMinutes(30), clock);
        await sessions.OpenAsync("conversation-1", Token);

        clock.Advance(TimeSpan.FromMinutes(31));
        await sessions.SweepAsync(Token);

        // A caller that abandons a text conversation never reaches a terminal stage, so nothing else would
        // ever drop this session and the process would hold it for its whole life.
        Assert.Null(await sessions.TryGetAsync("conversation-1", Token));
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public async Task ReadingASessionPutsItsIdleClockBackToZero()
    {
        var clock = Clock();
        InMemoryConversationSessions sessions = new(Factory(), TimeSpan.FromMinutes(30), clock);
        await sessions.OpenAsync("conversation-1", Token);

        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(await sessions.TryGetAsync("conversation-1", Token));

        // Forty minutes since it was opened, twenty since it was last read. A conversation still being had
        // must not be dropped out from under the caller.
        clock.Advance(TimeSpan.FromMinutes(20));
        await sessions.SweepAsync(Token);

        Assert.NotNull(await sessions.TryGetAsync("conversation-1", Token));
    }

    [Fact]
    public async Task AnExpiringSessionStillHandsOverTheWordsItOwed()
    {
        // The reason expiry goes through CloseAsync. An evictor that dropped the entry on its own
        // would return with the write still in flight, and nothing left able to wait for it.
        ParkingConversationStore transcript = new();
        var clock = Clock();
        InMemoryConversationSessions sessions = new(Factory(transcript), TimeSpan.FromMinutes(30), clock);
        var session = await sessions.OpenAsync("conversation-1", Token);

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
        // §11 item 6 makes conversation.ended the last event of every conversation. A caller who simply stops
        // replying closes no socket and reaches no terminal stage, so expiry is the only thing left
        // that can write it, and a chain with no end is a permanent gap in the record of D23.
        RecordingConversationObserver observer = new();
        var clock = Clock();
        InMemoryConversationSessions sessions = new(
            Factory(observer: observer), TimeSpan.FromMinutes(30), clock);
        await sessions.OpenAsync("conversation-1", Token);

        clock.Advance(TimeSpan.FromMinutes(31));
        await sessions.SweepAsync(Token);

        Assert.Contains(observer.Kinds, kind => kind == ConversationEventKind.ConversationEnded);
    }


    private static FakeTimeProvider Clock()
        => new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    private const string Document = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

    private static ConversationSessionFactory Factory(
        IConversationStore? transcript = null,
        string? yaml = null,
        IChatClient? reply = null,
        IConversationObserver? observer = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml ?? Document);
        RoutingChatClientFactory chatClients = new(reply ?? new StubChatClient());
        var compiled = ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(chatClients) { ConversationStore = transcript })["main"];

        return new ConversationSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            ConversationSessionFactory.CreateExtractor(compiled, chatClients),
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

    /// <summary>Records the kind of every event one conversation raised.</summary>
    private sealed class RecordingConversationObserver : IConversationObserver
    {
        private readonly List<ConversationEventKind> _kinds = [];

        public IReadOnlyList<ConversationEventKind> Kinds
        {
            get
            {
                lock (_kinds)
                {
                    return [.. _kinds];
                }
            }
        }

        public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
        {
            lock (_kinds)
            {
                _kinds.Add(conversationEvent.Kind);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The default store, counting the reads that keep a conversation out of the idle sweep.</summary>
}

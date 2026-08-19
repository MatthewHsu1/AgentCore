using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Call;

/// <summary>
/// Pins what the provider adds to <see cref="CallTranscript"/>: the gate, the writes, and the rule
/// that a store failure never reaches the turn.
/// </summary>
public sealed class AgentCoreChatHistoryProviderTests
{
    private const string CallId = "call-1";

    [Fact]
    public async Task ProvideChatHistory_AfterStore_ReturnsMessagesInOrder()
    {
        // Arrange
        var (provider, _, session) = NewCall();
        await StoreTurnAsync(provider, session, turnIndex: 0, "hello", "hi there");
        await StoreTurnAsync(provider, session, turnIndex: 1, "order 41?", "it ships Friday");

        // Act
        var history = await ProvideAsync(provider, session);

        // Assert
        Assert.Equal(
            ["hello", "hi there", "order 41?", "it ships Friday"],
            history.Select(message => message.Text));
    }

    [Fact]
    public async Task StoreChatHistory_MultipleTurns_OrdinalsAreDenseAndUnique()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        await StoreTurnAsync(provider, session, turnIndex: 0, "hello", "hi there");

        // Act
        await StoreTurnAsync(provider, session, turnIndex: 1, "order 41?", "it ships Friday");

        // Assert
        Assert.Equal([0, 1, 2, 3], store.Rows.Select(row => row.Ordinal));
        Assert.Equal([0, 0, 1, 1], store.Rows.Select(row => row.TurnIndex));
        Assert.All(store.Rows, row => Assert.Equal(CallId, row.CallId));
    }

    [Fact]
    public async Task StoreChatHistory_RefusedTurn_IsStored()
    {
        // Arrange
        var (provider, store, session) = NewCall();

        // Act
        await StoreTurnAsync(provider, session, turnIndex: 0, "something flagged", "I can't help with that.");

        // Assert
        Assert.Equal(
            ["something flagged", "I can't help with that."],
            store.Rows.Select(row => row.Content.Text));
    }

    [Fact]
    public async Task StoreChatHistory_FailedTurn_IsStored()
    {
        // Arrange
        var (provider, store, session) = NewCall();

        // Act
        await StoreTurnAsync(provider, session, turnIndex: 0, "check my order", "Sorry, I had trouble with that.");

        // Assert
        Assert.Equal(
            ["check my order", "Sorry, I had trouble with that."],
            store.Rows.Select(row => row.Content.Text));
    }

    [Fact]
    public async Task TruncateLastReply_LastAssistantMessage_RewritesOnlyThatMessage()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        await StoreTurnAsync(provider, session, turnIndex: 0, "hello", "hi there");
        await StoreTurnAsync(provider, session, turnIndex: 1, "order 41?", "it ships Friday from the depot");

        // Act
        var cut = await provider.TruncateLastReplyAsync(
            session, "it ships", TimeSpan.FromMilliseconds(420), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(cut);
        var history = await ProvideAsync(provider, session);
        Assert.Equal(
            ["hello", "hi there", "order 41?", "it ships"],
            history.Select(message => message.Text));
        Assert.Equal([3], store.Rewrites.Select(rewrite => rewrite.Ordinal));
    }

    [Fact]
    public async Task ProvideChatHistory_AfterTruncate_ReturnsHeardTextNotProducedText()
    {
        // Arrange
        var (provider, _, session) = NewCall();
        await StoreTurnAsync(provider, session, turnIndex: 0, "order 41?", "it ships Friday from the depot");

        // Act
        _ = await provider.TruncateLastReplyAsync(
            session, "it ships Fri", TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        // Assert
        var history = await ProvideAsync(provider, session);
        Assert.Equal(["order 41?", "it ships Fri"], history.Select(message => message.Text));
    }

    /// <summary>
    /// The barge-in lands while the turn it belongs to is still inside its own write. The gate is
    /// what makes it wait: a rewrite that overtakes the insert it targets finds no row, changes
    /// nothing, and leaves the record holding words the caller never heard.
    /// </summary>
    [Fact]
    public async Task TruncateLastReply_RacingAppend_NeverRewritesPreviousTurn()
    {
        // Arrange
        var store = new BlockingCallMessageStore();
        var provider = new AgentCoreChatHistoryProvider(store);
        var session = new StubSession();
        await StoreTurnAsync(provider, session, turnIndex: 0, "hello", "hi there");
        await provider.BeginTurnAsync(session, CallId, turnIndex: 1, TestContext.Current.CancellationToken);
        store.BlockNextAppend();
        var append = InvokeTurnAsync(provider, session, "order 41?", "it ships Friday from the depot");
        await store.Entered;

        // Act
        var truncate = provider.TruncateLastReplyAsync(
            session, "it ships", TimeSpan.FromMilliseconds(90), TestContext.Current.CancellationToken).AsTask();
        store.Release();

        // Assert
        await append;
        Assert.True(await truncate);
        Assert.Equal(
            ["hello", "hi there", "order 41?", "it ships"],
            store.Read(CallId).Select(row => row.Content.Text));
    }

    [Fact]
    public async Task TruncateLastReply_BeforeAnyReplyExists_NoOps()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        await provider.BeginTurnAsync(session, CallId, turnIndex: 0, TestContext.Current.CancellationToken);

        // Act
        var cut = await provider.TruncateLastReplyAsync(
            session, "nothing was said", TimeSpan.Zero, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(cut);
        Assert.Empty(store.Rewrites);
        Assert.Empty(await ProvideAsync(provider, session));
    }

    [Fact]
    public async Task StoreChatHistory_ConcurrentAppends_LosesNoMessage()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        await provider.BeginTurnAsync(session, CallId, turnIndex: 0, TestContext.Current.CancellationToken);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = Enumerable.Range(0, 20)
            .Select(index => Task.Run(
                async () =>
                {
                    await start.Task;
                    await InvokeTurnAsync(provider, session, $"said {index}", $"replied {index}");
                },
                TestContext.Current.CancellationToken))
            .ToArray();

        // Act
        start.SetResult();
        await Task.WhenAll(turns);

        // Assert
        Assert.Equal(40, store.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 40), store.Rows.Select(row => row.Ordinal).Order());
    }

    [Fact]
    public async Task ProvideChatHistory_TwoConcurrentSessions_DoNotMix()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new RecordingCallMessageStore());
        var first = new StubSession();
        var second = new StubSession();
        await StoreTurnAsync(provider, first, turnIndex: 0, "a said", "a heard", "call-a");

        // Act
        await StoreTurnAsync(provider, second, turnIndex: 0, "b said", "b heard", "call-b");

        // Assert
        Assert.Equal(["a said", "a heard"], (await ProvideAsync(provider, first)).Select(message => message.Text));
        Assert.Equal(["b said", "b heard"], (await ProvideAsync(provider, second)).Select(message => message.Text));
    }

    [Fact]
    public async Task StoreChatHistory_BackingStoreThrows_DoesNotFailTheTurn()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new ThrowingCallMessageStore());
        var session = new StubSession();

        // Act
        await StoreTurnAsync(provider, session, turnIndex: 0, "hello", "hi there");

        // Assert
        Assert.Equal(["hello", "hi there"], (await ProvideAsync(provider, session)).Select(message => message.Text));
    }

    [Fact]
    public async Task AppendCallerFacingTurn_GraphTurn_StoresOnlyTheTwoMessagesGiven()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        await provider.BeginTurnAsync(session, CallId, turnIndex: 0, TestContext.Current.CancellationToken);

        // Act
        await provider.AppendCallerFacingTurnAsync(
            session,
            new ChatMessage(ChatRole.User, "order 41?"),
            new ChatMessage(ChatRole.Assistant, "it ships Friday"),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["order 41?", "it ships Friday"], store.Rows.Select(row => row.Content.Text));
        Assert.Equal([0, 1], store.Rows.Select(row => row.Ordinal));
    }

    [Fact]
    public async Task InMemoryStore_AfterTurnAndBargeIn_HoldsTheHeardTextInOrder()
    {
        // Arrange
        var store = new InMemoryCallMessageStore();
        var provider = new AgentCoreChatHistoryProvider(store);
        var session = new StubSession();
        await StoreTurnAsync(provider, session, turnIndex: 0, "order 41?", "it ships Friday from the depot");

        // Act
        _ = await provider.TruncateLastReplyAsync(
            session, "it ships", TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["order 41?", "it ships"], store.Read(CallId).Select(row => row.Content.Text));
    }

    private static (AgentCoreChatHistoryProvider Provider, RecordingCallMessageStore Store, StubSession Session) NewCall()
    {
        var store = new RecordingCallMessageStore();
        return (new AgentCoreChatHistoryProvider(store), store, new StubSession());
    }

    /// <summary>Runs one turn's worth of provider calls: stamp the turn, then store what it produced.</summary>
    private static async Task StoreTurnAsync(
        AgentCoreChatHistoryProvider provider,
        AgentSession session,
        int turnIndex,
        string said,
        string replied,
        string callId = CallId)
    {
        await provider.BeginTurnAsync(session, callId, turnIndex, TestContext.Current.CancellationToken);
        await InvokeTurnAsync(provider, session, said, replied);
    }

    /// <summary>Hands the provider one finished run, the way the framework does.</summary>
    private static async Task InvokeTurnAsync(
        AgentCoreChatHistoryProvider provider, AgentSession session, string said, string replied)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        var context = new ChatHistoryProvider.InvokedContext(
            StubAgent.Instance,
            session,
            [new ChatMessage(ChatRole.User, said)],
            [new ChatMessage(ChatRole.Assistant, replied)]);
#pragma warning restore MAAI001
        await provider.InvokedAsync(context, TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<ChatMessage>> ProvideAsync(
        AgentCoreChatHistoryProvider provider, AgentSession session)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        var context = new ChatHistoryProvider.InvokingContext(StubAgent.Instance, session, []);
#pragma warning restore MAAI001
        var messages = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);
        return [.. messages];
    }

    private sealed class RecordingCallMessageStore : ICallMessageStore
    {
        private readonly Lock _lock = new();

        public List<CallMessage> Rows { get; } = [];

        public List<CallMessage> Rewrites { get; } = [];

        public ValueTask AppendAsync(
            IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                Rows.AddRange(messages);
            }

            return default;
        }

        public ValueTask RewriteAsync(
            string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                Rewrites.Add(new CallMessage(callId, ordinal, TurnIndex: -1, content));
            }

            return default;
        }
    }

    /// <summary>
    /// Holds one append open, so a barge-in can arrive while a turn is still writing. It keeps the
    /// real store's ordering rule: a rewrite of a row that is not there yet changes nothing.
    /// </summary>
    private sealed class BlockingCallMessageStore : ICallMessageStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InMemoryCallMessageStore _rows = new();
        private bool _block;

        public Task Entered => _entered.Task;

        public void BlockNextAppend() => _block = true;

        public void Release() => _release.TrySetResult();

        public IReadOnlyList<CallMessage> Read(string callId) => _rows.Read(callId);

        public async ValueTask AppendAsync(
            IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
        {
            if (_block)
            {
                _block = false;
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            await _rows.AppendAsync(messages, cancellationToken);
        }

        public ValueTask RewriteAsync(
            string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
            => _rows.RewriteAsync(callId, ordinal, content, cancellationToken);
    }

    private sealed class ThrowingCallMessageStore : ICallMessageStore
    {
        public ValueTask AppendAsync(
            IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the transcript store is down.");

        public ValueTask RewriteAsync(
            string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the transcript store is down.");
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

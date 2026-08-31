using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Pins what the provider adds to <see cref="CallTranscript"/>: the lock, the write chain, and the
/// rule that a store failure never reaches the turn.
/// </summary>
public sealed class AgentCoreChatHistoryProviderTests
{
    private const string CallId = "call-1";

    /// <summary>
    /// The key the call's transcript is filed under in <see cref="AgentSession.StateBag"/>. Changing
    /// it orphans the state of any session already persisted under the old one.
    /// </summary>
    private const string StateKey = "AgentCoreChatHistoryProvider";

    [Fact]
    public async Task ProvideChatHistory_AfterAppend_ReturnsMessagesInOrder()
    {
        // Arrange
        var (provider, _, session) = NewCall();
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");
        AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday");

        // Act
        var history = await ProvideAsync(provider, session);

        // Assert
        Assert.Equal(
            ["hello", "hi there", "order 41?", "it ships Friday"],
            history.Select(message => message.Text));
    }

    [Fact]
    public async Task AppendTurn_MultipleTurns_OrdinalsAreDenseAndUnique()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");

        // Act
        AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal([0, 1, 2, 3], store.Rows.Select(row => row.Ordinal));
        Assert.Equal([0, 0, 1, 1], store.Rows.Select(row => row.TurnIndex));
        Assert.All(store.Rows, row => Assert.Equal(CallId, row.CallId));
    }

    [Fact]
    public async Task AppendTurn_RefusedTurn_IsStored()
    {
        // Arrange
        var (provider, store, session) = NewCall();

        // Act
        AppendTurn(provider, session, turnIndex: 0, "something flagged", "I can't help with that.");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(
            ["something flagged", "I can't help with that."],
            store.Rows.Select(row => row.Content.Text));
    }

    [Fact]
    public async Task AppendTurn_FailedTurn_IsStored()
    {
        // Arrange
        var (provider, store, session) = NewCall();

        // Act
        AppendTurn(provider, session, turnIndex: 0, "check my order", "Sorry, I had trouble with that.");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(
            ["check my order", "Sorry, I had trouble with that."],
            store.Rows.Select(row => row.Content.Text));
    }

    /// <summary>
    /// The framework offers to store a finished run, and this provider declines. Measured on
    /// Microsoft.Agents.AI 1.17.0, that hook stores the request verbatim — reminder and all — and is
    /// never called at all for a run the caller cut short, which is every barge-in. CallSession
    /// writes the turn it shaped instead.
    /// </summary>
    [Fact]
    public async Task StoreChatHistory_FinishedRun_StoresNothing()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        provider.BeginTurn(session, turnIndex: 0);

        // Act
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(
                StubAgent.Instance,
                session,
                [new ChatMessage(ChatRole.User, "<system-reminder>ask for the id</system-reminder>\norder 41?")],
                [new ChatMessage(ChatRole.Assistant, "it ships Friday")]),
            TestContext.Current.CancellationToken);
#pragma warning restore MAAI001

        // Assert
        await provider.DrainAsync(session);
        Assert.Empty(store.Rows);
        Assert.Empty(await ProvideAsync(provider, session));
    }

    [Fact]
    public async Task TruncateLastReply_LastAssistantMessage_RewritesOnlyThatMessage()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");
        AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday from the depot");

        // Act
        var cut = provider.TruncateLastReply(session, "it ships", TimeSpan.FromMilliseconds(420));

        // Assert
        Assert.True(cut);
        var history = await ProvideAsync(provider, session);
        Assert.Equal(
            ["hello", "hi there", "order 41?", "it ships"],
            history.Select(message => message.Text));
        await provider.DrainAsync(session);
        Assert.Equal([3], store.Rewrites.Select(rewrite => rewrite.Ordinal));
    }

    [Fact]
    public async Task ProvideChatHistory_AfterTruncate_ReturnsHeardTextNotProducedText()
    {
        // Arrange
        var (provider, _, session) = NewCall();
        AppendTurn(provider, session, turnIndex: 0, "order 41?", "it ships Friday from the depot");

        // Act
        _ = provider.TruncateLastReply(session, "it ships Fri", TimeSpan.FromMilliseconds(300));

        // Assert
        var history = await ProvideAsync(provider, session);
        Assert.Equal(["order 41?", "it ships Fri"], history.Select(message => message.Text));
    }

    /// <summary>
    /// The barge-in lands while the turn it belongs to is still inside its own write. The write
    /// chain is what orders the two: a rewrite that overtook the insert it targets would find no row
    /// and leave the record holding words the caller never heard.
    /// </summary>
    [Fact]
    public async Task TruncateLastReply_WhileTheAppendIsStillWriting_ReachesTheStoreAfterIt()
    {
        // Arrange
        var store = new BlockingCallStore();
        var provider = new AgentCoreChatHistoryProvider(store);
        var session = new StubSession();
        provider.BeginCall(session, CallId, []);
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");
        await provider.DrainAsync(session);
        store.BlockNextAppend();
        AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday from the depot");
        await store.Entered;

        // Act
        var cut = provider.TruncateLastReply(session, "it ships", TimeSpan.FromMilliseconds(90));
        store.Release();
        await provider.DrainAsync(session);

        // Assert
        Assert.True(cut);
        Assert.Equal(
            ["hello", "hi there", "order 41?", "it ships"],
            (await store.ReadAsync(CallId, TestContext.Current.CancellationToken)).Select(row => row.Content.Text));
    }

    /// <summary>
    /// The held prompt of item 6a: the vendor is still speaking turn 0 when turn 1 begins, so the
    /// reply the caller was hearing belongs to the turn before the one now open. CallSession decides
    /// that a barge-in reaches it; the provider must not refuse because the turn moved on.
    /// </summary>
    [Fact]
    public async Task TruncateLastReply_AfterTheNextTurnOpened_CutsTheReplyTheCallerWasHearing()
    {
        // Arrange
        var (provider, _, session) = NewCall();
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there caller");
        provider.BeginTurn(session, turnIndex: 1);

        // Act
        var cut = provider.TruncateLastReply(session, "hi there", TimeSpan.FromMilliseconds(300));

        // Assert
        Assert.True(cut);
        var history = await ProvideAsync(provider, session);
        Assert.Equal(["hello", "hi there"], history.Select(message => message.Text));
    }

    /// <summary>
    /// A model routinely writes a line and puts the tool call it announces on the same message, and a
    /// graph row writes one reply for each node. The caller heard as much of the turn as the vendor
    /// played and nothing else, so no earlier word of that turn may survive the cut as text the
    /// caller is recorded as having heard.
    /// </summary>
    [Fact]
    public async Task TruncateLastReply_TurnWithProseBesideAToolCall_DropsEveryWordButTheHeardOnes()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        provider.BeginTurn(session, turnIndex: 0);
        ChatMessage announced = new(
            ChatRole.Assistant,
            [new TextContent("Let me check that for you"), new FunctionCallContent("call-1", "lookup")]);
        provider.AppendTurn(
            session,
            [
                new ChatMessage(ChatRole.User, "how much?"),
                announced,
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "50")]),
                new ChatMessage(ChatRole.Assistant, "the price is fifty"),
            ]);

        // Act
        var cut = provider.TruncateLastReply(session, "the price", TimeSpan.FromMilliseconds(400));

        // Assert
        Assert.True(cut);
        var history = await ProvideAsync(provider, session);
        Assert.Equal(["how much?", string.Empty, string.Empty, "the price"], history.Select(m => m.Text));

        // The side effect ran, so the pair stays. That is the rule the cut must not break.
        Assert.Contains(history, m => m.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(history, m => m.Contents.OfType<FunctionResultContent>().Any());

        await provider.DrainAsync(session);
        Assert.Equal([1, 3], store.Rewrites.Select(rewrite => rewrite.Ordinal));
    }

    [Fact]
    public async Task TruncateLastReply_BeforeAnyReplyExists_NoOps()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        provider.BeginTurn(session, turnIndex: 0);

        // Act
        var cut = provider.TruncateLastReply(session, "nothing was said", TimeSpan.Zero);

        // Assert
        Assert.False(cut);
        Assert.Empty(store.Rewrites);
        Assert.Empty(await ProvideAsync(provider, session));
    }

    [Fact]
    public async Task AppendTurn_ConcurrentAppends_LosesNoMessage()
    {
        // Arrange
        var (provider, store, session) = NewCall();
        provider.BeginTurn(session, turnIndex: 0);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = Enumerable.Range(0, 20)
            .Select(index => Task.Run(
                async () =>
                {
                    await start.Task;
                    provider.AppendTurn(
                        session,
                        [
                            new ChatMessage(ChatRole.User, $"said {index}"),
                            new ChatMessage(ChatRole.Assistant, $"replied {index}"),
                        ]);
                },
                TestContext.Current.CancellationToken))
            .ToArray();

        // Act
        start.SetResult();
        await Task.WhenAll(turns);

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(40, store.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 40), store.Rows.Select(row => row.Ordinal).Order());
    }

    [Fact]
    public async Task ProvideChatHistory_TwoConcurrentSessions_DoNotMix()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new RecordingCallStore());
        var first = new StubSession();
        var second = new StubSession();
        provider.BeginCall(first, "call-a", []);
        provider.BeginCall(second, "call-b", []);
        AppendTurn(provider, first, turnIndex: 0, "a said", "a heard");

        // Act
        AppendTurn(provider, second, turnIndex: 0, "b said", "b heard");

        // Assert
        Assert.Equal(["a said", "a heard"], (await ProvideAsync(provider, first)).Select(message => message.Text));
        Assert.Equal(["b said", "b heard"], (await ProvideAsync(provider, second)).Select(message => message.Text));
    }

    /// <summary>
    /// The framework validates this set at agent construction and again on every run, and refuses a
    /// collision. The value is also the name a persisted session's state is filed under, so a change
    /// here is a silent data loss rather than a rename.
    /// </summary>
    [Fact]
    public void StateKeys_IsTheSingleKeyTheTranscriptIsFiledUnder()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider();

        // Assert
        Assert.Equal([StateKey], provider.StateKeys);
    }

    [Fact]
    public void AppendTurn_PutsTheTranscriptInTheSessionStateBagUnderTheProviderKey()
    {
        // Arrange
        var (provider, _, session) = NewCall();

        // Act
        AppendTurn(provider, session, turnIndex: 0, "order 41?", "it ships Friday");

        // Assert
        Assert.True(session.StateBag.TryGetValue<CallTranscript>(StateKey, out var transcript, StateOptions));
        Assert.NotNull(transcript);
        Assert.Equal(CallId, transcript.CallId);
        Assert.Equal(["order 41?", "it ships Friday"], transcript.Read().Select(message => message.Text));
    }

    /// <summary>
    /// The provider is one object shared by every call, so the same key on two sessions must reach
    /// two transcripts. A key resolved against the provider rather than the session would merge them.
    /// </summary>
    [Fact]
    public void AppendTurn_TwoSessions_EachHoldsItsOwnTranscriptUnderThatKey()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new RecordingCallStore());
        var first = new StubSession();
        var second = new StubSession();
        provider.BeginCall(first, "call-a", []);
        provider.BeginCall(second, "call-b", []);

        // Act
        AppendTurn(provider, first, turnIndex: 0, "a said", "a heard");
        AppendTurn(provider, second, turnIndex: 0, "b said", "b heard");

        // Assert
        Assert.Equal(["a said", "a heard"], TranscriptIn(first).Read().Select(message => message.Text));
        Assert.Equal(["b said", "b heard"], TranscriptIn(second).Read().Select(message => message.Text));
    }

    [Fact]
    public async Task AppendTurn_BackingStoreThrows_DoesNotFailTheTurn()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new ThrowingCallStore());
        var session = new StubSession();
        provider.BeginCall(session, CallId, []);

        // Act
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(["hello", "hi there"], (await ProvideAsync(provider, session)).Select(message => message.Text));
    }

    [Fact]
    public async Task BeginCall_BackingStoreThrows_TellsTheReporterWhichTurnWasLost()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new ThrowingCallStore());
        var session = new StubSession();
        List<int> dropped = [];
        provider.BeginCall(session, CallId, [], (turnIndex, _) => dropped.Add(turnIndex));

        // Act
        AppendTurn(provider, session, turnIndex: 3, "hello", "hi there");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal([3], dropped);
    }

    [Fact]
    public async Task InMemoryStore_AfterTurnAndBargeIn_HoldsTheHeardTextInOrder()
    {
        // Arrange
        var store = new InMemoryCallStore();
        var provider = new AgentCoreChatHistoryProvider(store);
        var session = new StubSession();
        provider.BeginCall(session, CallId, []);
        AppendTurn(provider, session, turnIndex: 0, "order 41?", "it ships Friday from the depot");

        // Act
        _ = provider.TruncateLastReply(session, "it ships", TimeSpan.FromMilliseconds(200));

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(
            ["order 41?", "it ships"],
            (await store.ReadAsync(CallId, TestContext.Current.CancellationToken)).Select(row => row.Content.Text));
    }

    /// <summary>Opens one call on a fresh session, the way <c>CallSession</c> does at call start.</summary>
    private static (AgentCoreChatHistoryProvider Provider, RecordingCallStore Store, StubSession Session) NewCall()
    {
        var store = new RecordingCallStore();
        var provider = new AgentCoreChatHistoryProvider(store);
        var session = new StubSession();
        provider.BeginCall(session, CallId, []);
        return (provider, store, session);
    }

    /// <summary>Writes one turn the way <c>CallSession</c> does: name the turn, then append it.</summary>
    private static void AppendTurn(
        AgentCoreChatHistoryProvider provider,
        AgentSession session,
        int turnIndex,
        string said,
        string replied)
    {
        provider.BeginTurn(session, turnIndex);
        provider.AppendTurn(
            session,
            [new ChatMessage(ChatRole.User, said), new ChatMessage(ChatRole.Assistant, replied)]);
    }

    /// <summary>The converters the provider files the transcript with.</summary>
    private static JsonSerializerOptions StateOptions => AIJsonUtilities.DefaultOptions;

    /// <summary>Reads one session's transcript straight out of its state bag, past the provider.</summary>
    private static CallTranscript TranscriptIn(AgentSession session)
    {
        Assert.True(session.StateBag.TryGetValue<CallTranscript>(StateKey, out var transcript, StateOptions));
        Assert.NotNull(transcript);
        return transcript;
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

    /// <summary>
    /// Holds one append open, so a barge-in can arrive while a turn is still writing. It keeps the
    /// real store's ordering rule: a rewrite of a row that is not there yet changes nothing.
    /// </summary>
    private sealed class BlockingCallStore() : DelegatingCallStore(new InMemoryCallStore())
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _block;

        public Task Entered => _entered.Task;

        public void BlockNextAppend() => _block = true;

        public void Release() => _release.TrySetResult();

        public override async ValueTask AppendAsync(
            IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
        {
            if (_block)
            {
                _block = false;
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            await Inner.AppendAsync(messages, cancellationToken);
        }

        public override ValueTask RewriteAsync(
            string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
            => Inner.RewriteAsync(callId, ordinal, content, cancellationToken);
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

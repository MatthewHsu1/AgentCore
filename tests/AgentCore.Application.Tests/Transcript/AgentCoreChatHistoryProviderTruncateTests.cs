using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.AgentCoreChatHistoryProviderTestSupport;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Pins what a barge-in does to the words the provider already wrote: which row it rewrites, and
/// when the rewrite is guaranteed to land after the append it corrects.
/// </summary>
public sealed class AgentCoreChatHistoryProviderTruncateTests
{
    [Fact]
    public async Task TruncateLastReply_LastAssistantMessage_RewritesOnlyThatMessage()
    {
        // Arrange
        var (provider, store, session) = await NewCall();
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
        var (provider, _, session) = await NewCall();
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
        await store.CreateAsync(CallId, TestContext.Current.CancellationToken);
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
        var (provider, _, session) = await NewCall();
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
        var (provider, store, session) = await NewCall();
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
        var (provider, store, session) = await NewCall();
        provider.BeginTurn(session, turnIndex: 0);

        // Act
        var cut = provider.TruncateLastReply(session, "nothing was said", TimeSpan.Zero);

        // Assert
        Assert.False(cut);
        Assert.Empty(store.Rewrites);
        Assert.Empty(await ProvideAsync(provider, session));
    }

    [Fact]
    public async Task InMemoryStore_AfterTurnAndBargeIn_HoldsTheHeardTextInOrder()
    {
        // Arrange
        var store = new InMemoryCallStore();
        await store.CreateAsync(CallId, TestContext.Current.CancellationToken);
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
}

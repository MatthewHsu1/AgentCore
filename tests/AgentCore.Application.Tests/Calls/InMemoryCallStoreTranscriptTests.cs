using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Calls;

/// <summary>The words half of store 0, now that one store holds both halves.</summary>
public sealed class InMemoryCallStoreTranscriptTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadAsync_AfterAppend_ReturnsTheRowsOldestFirst()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);

        // Act
        await store.AppendAsync(
            "c1",
            [
                new CallMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0"),
                new CallMessageDraft(0, new ChatMessage(ChatRole.Assistant, "hi"), "m1"),
            ], cancellationToken: Token);

        // Assert
        var rows = await store.ReadAsync("c1", Token);
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows[0].Ordinal);
        Assert.Equal("hello", rows[0].Content.Text);
    }

    [Fact]
    public async Task RewriteAsync_AnExistingMessage_ReplacesItsContent()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.AppendAsync(
            "c1",
            [new CallMessageDraft(0, new ChatMessage(ChatRole.Assistant, "long reply"), "m0")],
            cancellationToken: Token);

        // Act
        await store.RewriteAsync("c1", "m0", new ChatMessage(ChatRole.Assistant, "cut"), Token);

        // Assert
        var rows = await store.ReadAsync("c1", Token);
        Assert.Equal("cut", Assert.Single(rows).Content.Text);
    }

    [Fact]
    public async Task EraseAsync_ACallWithWords_RemovesThemAndReportsTheCount()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.CreateAsync("c2", Token);
        await store.AppendAsync(
            "c1", [new CallMessageDraft(0, new ChatMessage(ChatRole.User, "a"), "m0")], cancellationToken: Token);
        await store.AppendAsync(
            "c2", [new CallMessageDraft(0, new ChatMessage(ChatRole.User, "b"), "m0")], cancellationToken: Token);

        // Act
        var erased = await store.EraseAsync("c1", Token);

        // Assert
        Assert.Equal(1, erased);
        Assert.Empty(await store.ReadAsync("c1", Token));
        Assert.Single(await store.ReadAsync("c2", Token));
    }

    [Fact]
    public async Task GetAsync_AfterAppend_ReportsWhenTheCallWasLastSpokenOn()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);

        // Act
        await store.AppendAsync(
            "c1", [new CallMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")], cancellationToken: Token);

        // Assert
        var call = await store.GetAsync("c1", Token);
        Assert.NotNull(call);
        Assert.NotNull(call.LastMessageAt);
    }

    [Fact]
    public async Task ReadAsync_ACallWithMessages_ReturnsThemOldestFirst()
    {
        // Arrange — the store numbers rows in draft order, so this pins the read order against the
        // ordinal the store assigned rather than against the order the drafts happened to list.
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.AppendAsync(
            "c1",
            [
                new CallMessageDraft(0, new ChatMessage(ChatRole.User, "first"), "m0"),
                new CallMessageDraft(0, new ChatMessage(ChatRole.User, "second"), "m1"),
            ],
            cancellationToken: Token);

        // Act
        var rows = await store.ReadAsync("c1", Token);

        // Assert
        Assert.Equal([0, 1], rows.Select(row => row.Ordinal));
        Assert.Equal(["first", "second"], rows.Select(row => row.Content.Text));
    }

    [Fact]
    public async Task ReadAsync_ACallThatHoldsNothing_IsEmpty()
    {
        // Arrange
        InMemoryCallStore store = new();

        // Act
        var rows = await store.ReadAsync("missing", Token);

        // Assert
        Assert.Empty(rows);
    }

    [Fact]
    public async Task ReadAsync_AnotherCallsMessages_AreNotReturned()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.CreateAsync("c2", Token);
        await store.AppendAsync(
            "c1", [new CallMessageDraft(0, new ChatMessage(ChatRole.User, "mine"), "m0")], cancellationToken: Token);
        await store.AppendAsync(
            "c2", [new CallMessageDraft(0, new ChatMessage(ChatRole.User, "theirs"), "m0")], cancellationToken: Token);

        // Act
        var rows = await store.ReadAsync("c1", Token);

        // Assert
        Assert.Single(rows);
    }

    [Fact]
    public async Task EraseAsync_ACall_TakesEveryRowAndReportsHowMany()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.AppendAsync(
            "c1",
            [
                new CallMessageDraft(0, new ChatMessage(ChatRole.User, "one"), "m0"),
                new CallMessageDraft(0, new ChatMessage(ChatRole.Assistant, "two"), "m1"),
            ],
            cancellationToken: Token);

        // Act
        var erased = await store.EraseAsync("c1", Token);

        // Assert
        Assert.Equal(2, erased);
        Assert.Empty(await store.ReadAsync("c1", Token));
    }

    [Fact]
    public async Task EraseAsync_ACall_LeavesEveryOtherCallAlone()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.CreateAsync("c2", Token);
        await store.AppendAsync(
            "c1", [new CallMessageDraft(0, new ChatMessage(ChatRole.User, "mine"), "m0")], cancellationToken: Token);
        await store.AppendAsync(
            "c2", [new CallMessageDraft(0, new ChatMessage(ChatRole.User, "theirs"), "m0")], cancellationToken: Token);

        // Act
        await store.EraseAsync("c1", Token);

        // Assert
        Assert.Single(await store.ReadAsync("c2", Token));
    }
}

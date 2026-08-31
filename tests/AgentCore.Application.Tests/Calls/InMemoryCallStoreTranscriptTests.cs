using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Ports;
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

        // Act
        await store.AppendAsync(
        [
            new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "hello")),
            new CallMessage("c1", 1, 0, new ChatMessage(ChatRole.Assistant, "hi")),
        ], Token);

        // Assert
        var rows = await store.ReadAsync("c1", Token);
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows[0].Ordinal);
        Assert.Equal("hello", rows[0].Content.Text);
    }

    [Fact]
    public async Task RewriteAsync_AnExistingOrdinal_ReplacesItsContent()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.AppendAsync(
            [new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.Assistant, "long reply"))], Token);

        // Act
        await store.RewriteAsync("c1", 0, new ChatMessage(ChatRole.Assistant, "cut"), Token);

        // Assert
        var rows = await store.ReadAsync("c1", Token);
        Assert.Equal("cut", Assert.Single(rows).Content.Text);
    }

    [Fact]
    public async Task EraseAsync_ACallWithWords_RemovesThemAndReportsTheCount()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.AppendAsync(
        [
            new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "a")),
            new CallMessage("c2", 0, 0, new ChatMessage(ChatRole.User, "b")),
        ], Token);

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
            [new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "hello"))], Token);

        // Assert
        var call = await store.GetAsync("c1", Token);
        Assert.NotNull(call);
        Assert.NotNull(call.LastMessageAt);
    }

    [Fact]
    public async Task ReadAsync_ACallWithMessages_ReturnsThemOldestFirst()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.AppendAsync(
            [
                new CallMessage("c1", 1, 0, new ChatMessage(ChatRole.User, "second")),
                new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "first")),
            ],
            Token);

        // Act
        var rows = await store.ReadAsync("c1", Token);

        // Assert
        Assert.Equal([0, 1], rows.Select(row => row.Ordinal));
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
        await store.AppendAsync([new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "mine"))], Token);
        await store.AppendAsync([new CallMessage("c2", 0, 0, new ChatMessage(ChatRole.User, "theirs"))], Token);

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
        await store.AppendAsync(
            [
                new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "one")),
                new CallMessage("c1", 1, 0, new ChatMessage(ChatRole.Assistant, "two")),
            ],
            Token);

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
        await store.AppendAsync([new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "mine"))], Token);
        await store.AppendAsync([new CallMessage("c2", 0, 0, new ChatMessage(ChatRole.User, "theirs"))], Token);

        // Act
        await store.EraseAsync("c1", Token);

        // Assert
        Assert.Single(await store.ReadAsync("c2", Token));
    }
}

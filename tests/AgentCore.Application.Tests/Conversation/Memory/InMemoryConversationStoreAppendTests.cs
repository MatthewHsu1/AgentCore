using System.Text.RegularExpressions;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Conversation.Memory;

/// <summary>
/// The store numbers every row from the conversation's own counter, in memory: the seam that lets a host
/// append to a conversation from outside any turn, whether or not a session for the conversation is live.
/// </summary>
#pragma warning disable CA1859 // store is typed as IConversationStore throughout: AppendAsync(conversationId, message, ct)
                               // is a default interface method, and only resolves through the interface type.
public sealed partial class InMemoryConversationStoreAppendTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppendAsync_OneMessage_OnAFreshConversation_IsOrdinalZeroAndTurnZero()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();
        await store.CreateAsync("c1", Token);

        // Act
        var row = await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "hello"), Token);

        // Assert
        Assert.Equal(0, row.Ordinal);
        Assert.Equal(0, row.TurnIndex);
        Assert.Matches(HexId(), row.MessageId);
    }

    [Fact]
    public async Task AppendAsync_OneMessage_WithItsOwnId_KeepsIt()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();
        await store.CreateAsync("c1", Token);
        ChatMessage message = new(ChatRole.User, "hello") { MessageId = "caller-named" };

        // Act
        var row = await store.AppendMessageAsync("c1", message, Token);

        // Assert
        Assert.Equal("caller-named", row.MessageId);
    }

    [Fact]
    public async Task AppendAsync_TwoOutsideAppends_AreNumberedZeroThenOne()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();
        await store.CreateAsync("c1", Token);

        // Act
        var first = await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "one"), Token);
        var second = await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "two"), Token);

        // Assert
        Assert.Equal(0, first.Ordinal);
        Assert.Equal(1, second.Ordinal);
        var conversation = await store.GetAsync("c1", Token);
        Assert.Equal(2, conversation?.NextOrdinal);
    }

    [Fact]
    public async Task AppendAsync_ABatchWithAnExplicitTurnIndex_NumbersThreeConsecutiveRowsInDraftOrder()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();
        await store.CreateAsync("c1", Token);
        await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "outside"), Token);

        ConversationMessageDraft[] drafts =
        [
            new(4, new ChatMessage(ChatRole.User, "a"), "m-a"),
            new(4, new ChatMessage(ChatRole.Assistant, "b"), "m-b"),
            new(4, new ChatMessage(ChatRole.User, "c"), "m-c"),
        ];

        // Act
        var rows = await store.AppendAsync("c1", drafts, cancellationToken: Token);

        // Assert
        Assert.Equal([1, 2, 3], rows.Select(row => row.Ordinal));
        Assert.All(rows, row => Assert.Equal(4, row.TurnIndex));
        Assert.Equal(["m-a", "m-b", "m-c"], rows.Select(row => row.MessageId));
    }

    [Fact]
    public async Task AppendAsync_OutsideAppend_AfterABatchThatWroteState_IsStampedWithTheStoredNextTurnIndex()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();
        await store.CreateAsync("c1", Token);
        await store.AppendAsync(
            "c1",
            [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
            new ConversationSessionState { NextTurnIndex = 7 },
            Token);

        // Act
        var row = await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "outside"), Token);

        // Assert
        Assert.Equal(7, row.TurnIndex);
    }

    [Fact]
    public async Task AppendAsync_AConversationThatDoesNotExist_ThrowsNamingIt()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();

        // Act
        var failure = await Record.ExceptionAsync(
            () => store.AppendMessageAsync("missing", new ChatMessage(ChatRole.User, "hello"), Token).AsTask());

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("missing", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewriteAsync_OneRow_ChangesOnlyThatRowsContent()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();
        await store.CreateAsync("c1", Token);
        await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "first") { MessageId = "m0" }, Token);
        await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "second") { MessageId = "m1" }, Token);

        // Act
        await store.RewriteAsync("c1", "m0", new ChatMessage(ChatRole.User, "corrected"), Token);

        // Assert
        var rows = await store.ReadAllAsync("c1", Token);
        Assert.Equal(["corrected", "second"], rows.Select(row => row.Content.Text));
    }

    [Fact]
    public async Task AppendAsync_AfterATruncate_NeverReusesTheOrdinalsItTook()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();
        await store.CreateAsync("c1", Token);
        await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "zero"), Token);
        await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "one"), Token);
        await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "two"), Token);
        await store.TruncateAsync("c1", 1, Token);

        // Act
        var row = await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "three"), Token);

        // Assert
        Assert.Equal(3, row.Ordinal);
    }

    [Fact]
    public async Task AppendAsync_ARowWithAdditionalProperties_KeepsThemAcrossTheRoundTrip()
    {
        // Arrange
        IConversationStore store = new InMemoryConversationStore();
        await store.CreateAsync("c1", Token);
        ChatMessage message = new(ChatRole.User, "hello")
        {
            AdditionalProperties = new() { ["speaker"] = "human" },
        };

        // Act
        await store.AppendMessageAsync("c1", message, Token);

        // Assert
        var rows = await store.ReadAllAsync("c1", Token);
        Assert.Equal("human", Assert.Single(rows).Content.AdditionalProperties?["speaker"]?.ToString());
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HexId();
}
#pragma warning restore CA1859

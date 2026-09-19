using System.Text.RegularExpressions;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres;

/// <summary>
/// The store numbers every row from the conversation's own counter, in PostgreSQL: the seam that lets a
/// host append to a conversation from outside any turn, on any machine, without colliding with the conversation's
/// live session.
/// </summary>
#pragma warning disable CA1859 // store is typed as IConversationStore throughout: AppendAsync(conversationId, message, ct)
                               // is a default interface method, and only resolves through the interface type.
public sealed partial class PostgresConversationStoreAppendTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task AppendAsync_OneMessage_OnAFreshConversation_IsOrdinalZeroAndTurnZero()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        var row = await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "hello"), Token);

        // Assert
        Assert.Equal(0, row.Ordinal);
        Assert.Equal(0, row.TurnIndex);
        Assert.Matches(HexId(), row.MessageId);
    }

    [PostgresFact]
    public async Task AppendAsync_OneMessage_WithItsOwnId_KeepsIt()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);
        await store.CreateAsync("c1", Token);
        ChatMessage message = new(ChatRole.User, "hello") { MessageId = "caller-named" };

        // Act
        var row = await store.AppendMessageAsync("c1", message, Token);

        // Assert
        Assert.Equal("caller-named", row.MessageId);
    }

    [PostgresFact]
    public async Task AppendAsync_TwoOutsideAppends_AreNumberedZeroThenOne()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);
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

    [PostgresFact]
    public async Task AppendAsync_ABatchWithAnExplicitTurnIndex_NumbersThreeConsecutiveRowsInDraftOrder()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);
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

    [PostgresFact]
    public async Task AppendAsync_OutsideAppend_AfterABatchThatWroteState_IsStampedWithTheStoredNextTurnIndex()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);
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

    [PostgresFact]
    public async Task AppendAsync_TwoWritersFiftyEach_LandsOneHundredRowsWithNoCollision()
    {
        // Arrange — the shape the owner proved under concurrency in a probe: 2 writers x 50 appends
        // -> 100 rows, 0..99, 0 retries. The store's data source pools connections, so two tasks
        // running concurrently naturally reach PostgreSQL on separate physical connections.
        IConversationStore store = new PostgresConversationStore(DataSource);
        await store.CreateAsync("c1", Token);

        async Task WriteFiftyAsync(string label)
        {
            for (var i = 0; i < 50; i++)
            {
                await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, $"{label}-{i}"), Token);
            }
        }

        // Act
        await Task.WhenAll(WriteFiftyAsync("a"), WriteFiftyAsync("b"));

        // Assert
        var rows = await store.ReadAsync("c1", Token);
        Assert.Equal(100, rows.Count);
        Assert.Equal(Enumerable.Range(0, 100), rows.Select(row => row.Ordinal).Order());
    }

    [PostgresFact]
    public async Task AppendAsync_AConversationThatDoesNotExist_ThrowsNamingIt()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);

        // Act
        var failure = await Record.ExceptionAsync(
            () => store.AppendMessageAsync("missing", new ChatMessage(ChatRole.User, "hello"), Token).AsTask());

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("missing", invalid.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task RewriteAsync_OneRow_ChangesOnlyThatRowsContent()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "first") { MessageId = "m0" }, Token);
        await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "second") { MessageId = "m1" }, Token);

        // Act
        await store.RewriteAsync("c1", "m0", new ChatMessage(ChatRole.User, "corrected"), Token);

        // Assert
        var rows = await store.ReadAsync("c1", Token);
        Assert.Equal(["corrected", "second"], rows.Select(row => row.Content.Text));
    }

    [PostgresFact]
    public async Task AppendAsync_AfterATruncate_NeverReusesTheOrdinalsItTook()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);
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

    [PostgresFact]
    public async Task ReadAsync_ARowWithAdditionalProperties_KeepsThemAcrossTheRoundTrip()
    {
        // Arrange
        IConversationStore store = new PostgresConversationStore(DataSource);
        await store.CreateAsync("c1", Token);
        ChatMessage message = new(ChatRole.User, "hello")
        {
            AdditionalProperties = new() { ["speaker"] = "human" },
        };

        // Act
        await store.AppendMessageAsync("c1", message, Token);

        // Assert
        var rows = await store.ReadAsync("c1", Token);
        Assert.Equal("human", Assert.Single(rows).Content.AdditionalProperties?["speaker"]?.ToString());
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HexId();
}
#pragma warning restore CA1859

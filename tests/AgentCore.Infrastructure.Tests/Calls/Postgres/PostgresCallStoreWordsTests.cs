using System.Text.Json;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.Infrastructure.Calls.Postgres;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Calls.Postgres;

/// <summary>
/// The words half of the store, in PostgreSQL.
/// </summary>
/// <remarks>
/// These need a live PostgreSQL and skip without one — <see cref="PostgresFactAttribute"/> names the
/// variable. Each test takes a database of its own, because the retention sweep and the erase both
/// read the whole table.
/// </remarks>
public sealed class PostgresCallStoreWordsTests : PostgresDatabaseTest
{
    private static readonly TimeSpan NinetyDays = TimeSpan.FromDays(90);

    /// <summary>Opens the store with the call rows these tests write words against already made.</summary>
    /// <remarks>
    /// call_message is a child of call, so a word cannot be written before its call exists. In
    /// production CallSession makes the row when the session opens; here the arrangement makes it.
    /// </remarks>
    private async Task<PostgresCallStore> OpenAsync()
    {
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("C1", Token);
        await store.CreateAsync("C2", Token);
        return store;
    }

    /// <inheritdoc />
    protected override bool Migrated => true;

    // ---------------------------------------------------------------------------------------------
    // Append.
    // ---------------------------------------------------------------------------------------------
    [PostgresFact]
    public async Task AppendAsync_ATurn_WritesOneRowForEachMessage()
    {
        // Arrange
        var store = await OpenAsync();

        // Act
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);

        // Assert
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
    }

    [PostgresFact]
    public async Task AppendAsync_ATurn_LiftsTheRoleOutOfTheContent()
    {
        // Arrange
        var store = await OpenAsync();

        // Act
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);

        // Assert — retention and redaction read the role, and never parse the content to find it.
        Assert.Equal(
            "user,assistant",
            await ScalarAsync<string>("SELECT string_agg(role, ',' ORDER BY ordinal) FROM call_message"));
    }

    [PostgresFact]
    public async Task AppendAsync_AToolCallingTurn_RoundTripsEveryContentPart()
    {
        // Arrange — the framework ships the polymorphic converters, so a tool call and its result
        // survive with no code of ours.
        var store = await OpenAsync();
        ChatMessage announced = new(
            ChatRole.Assistant,
            [new TextContent("Let me check that."), new FunctionCallContent("id1", "lookup", null)]);
        ChatMessage result = new(ChatRole.Tool, [new FunctionResultContent("id1", "Friday")]);

        // Act
        await store.AppendAsync(
            [
                new CallMessage("C1", 0, 0, announced),
                new CallMessage("C1", 1, 0, result),
            ],
            Token);

        // Assert
        var rows = await store.ReadAsync("C1", Token);
        Assert.Equal(
            ["Let me check that.", "lookup", "id1"],
            [
                rows[0].Content.Contents.OfType<TextContent>().Single().Text,
                rows[0].Content.Contents.OfType<FunctionCallContent>().Single().Name,
                rows[1].Content.Contents.OfType<FunctionResultContent>().Single().CallId,
            ]);
    }

    [PostgresFact]
    public async Task AppendAsync_NoMessages_TouchesNothing()
    {
        // Arrange
        var store = await OpenAsync();

        // Act
        await store.AppendAsync([], Token);

        // Assert
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
    }

    [PostgresFact]
    public async Task AppendAsync_AnOrdinalTheCallAlreadyUsed_IsRefused()
    {
        // Arrange — an ordinal is permanent, so a repeat is a defect and never a silent overwrite.
        // AgentCoreChatHistoryProvider is what catches this and lets the call continue.
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);

        // Act
        var failure = await Record.ExceptionAsync(
            () => store.AppendAsync(Turn("C1", turnIndex: 1, ordinal: 0), Token).AsTask());

        // Assert
        Assert.NotNull(failure);
    }

    // ---------------------------------------------------------------------------------------------
    // Read. It runs at call start, on a resume, and nowhere else.
    // ---------------------------------------------------------------------------------------------
    [PostgresFact]
    public async Task ReadAsync_AWrittenCall_ReturnsEveryRowInOrdinalOrder()
    {
        // Arrange
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);
        await store.AppendAsync(Turn("C1", turnIndex: 1, ordinal: 2), Token);

        // Act
        var rows = await store.ReadAsync("C1", Token);

        // Assert
        Assert.Equal([0, 1, 2, 3], rows.Select(row => row.Ordinal).ToArray());
    }

    [PostgresFact]
    public async Task ReadAsync_AWrittenCall_ReturnsTheOrdinalsAResumeGoesOnFrom()
    {
        // Arrange — a read that answered with the messages alone would restart ordinals at zero and
        // collide with the rows already there, on a primary key the provider never sees.
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 3, ordinal: 6), Token);

        // Act
        var rows = await store.ReadAsync("C1", Token);

        // Assert
        Assert.Equal([(6, 3), (7, 3)], rows.Select(row => (row.Ordinal, row.TurnIndex)).ToArray());
    }

    [PostgresFact]
    public async Task ReadAsync_AnotherCall_ReturnsNothing()
    {
        // Arrange
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);

        // Act
        var rows = await store.ReadAsync("C2", Token);

        // Assert
        Assert.Empty(rows);
    }

    // ---------------------------------------------------------------------------------------------
    // Rewrite. R4: the record holds the words the caller heard.
    // ---------------------------------------------------------------------------------------------
    [PostgresFact]
    public async Task RewriteAsync_ARow_ReplacesTheWordsOfThatRowOnly()
    {
        // Arrange
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);

        // Act
        await store.RewriteAsync("C1", 1, new ChatMessage(ChatRole.Assistant, "Order 41 sh"), Token);

        // Assert
        var rows = await store.ReadAsync("C1", Token);
        Assert.Equal(["what about order 41", "Order 41 sh"], rows.Select(row => row.Content.Text).ToArray());
    }

    [PostgresFact]
    public async Task RewriteAsync_ARow_MovesUpdatedAtAndLeavesCreatedAt()
    {
        // Arrange — the retention sweep reads updated_at, so a corrected turn ages from its
        // correction.
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);
        await ExecuteAsync("UPDATE call_message SET created_at = now() - interval '1 hour', updated_at = created_at");

        // Act
        await store.RewriteAsync("C1", 1, new ChatMessage(ChatRole.Assistant, "Order 41 sh"), Token);

        // Assert
        Assert.True(await ScalarAsync<bool>(
            "SELECT updated_at > created_at FROM call_message WHERE call_id = 'C1' AND ordinal = 1"));
    }

    [PostgresFact]
    public async Task RewriteAsync_AnOrdinalThatIsNotThere_WritesNothing()
    {
        // Arrange — a barge-in that raced the append it corrects. The append carries the corrected
        // words, so there is nothing to report and nothing to guess at.
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);

        // Act
        await store.RewriteAsync("C1", 9, new ChatMessage(ChatRole.Assistant, "never spoken"), Token);

        // Assert
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
    }

    // ---------------------------------------------------------------------------------------------
    // Erase, and the sweep.
    // ---------------------------------------------------------------------------------------------
    [PostgresFact]
    public async Task EraseAsync_OneCall_DeletesItsRowsAndLeavesTheOthers()
    {
        // Arrange
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), Token);
        await store.AppendAsync(Turn("C2", turnIndex: 0, ordinal: 0), Token);

        // Act
        var erased = await store.EraseAsync("C1", Token);

        // Assert
        Assert.Equal(2, erased);
        Assert.Empty(await store.ReadAsync("C1", Token));
        Assert.Equal(2, (await store.ReadAsync("C2", Token)).Count);
    }





    // ---------------------------------------------------------------------------------------------
    // Store 1 against store 3.
    // ---------------------------------------------------------------------------------------------
    [PostgresFact]
    public async Task ReadSpokenTurnsAsync_AToolCallingTurn_ReturnsOneRowForThatTurn()
    {
        // Arrange — the DISTINCT ON guard. The turn writes an assistant message carrying the tool
        // call and another carrying the reply; without it both come back, and the textless one
        // reports a false tamper on every tool-calling turn.
        var store = await OpenAsync();
        await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");

        // Act
        var turns = await store.ReadSpokenTurnsAsync("C1", Token);

        // Assert
        var turn = Assert.Single(turns);
        Assert.Equal("Order 41 ships Friday.", turn.Spoken);
    }

    [PostgresFact]
    public async Task ReadSpokenTurnsAsync_AToolCallingTurn_MatchesTheHashTheChainHolds()
    {
        // Arrange
        var store = await OpenAsync();
        await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");

        // Act
        var turns = await store.ReadSpokenTurnsAsync("C1", Token);

        // Assert — what the nightly check does: hash the words store 1 holds, and compare.
        Assert.Equal(AuditHash.OfText(turns[0].Spoken).Value, turns[0].ReplyTextSha256);
    }

    [PostgresFact]
    public async Task ReadSpokenTurnsAsync_AToolCallingTurnThatAlsoDrew_MatchesTheHashTheChainHolds()
    {
        // Arrange — a RenderContent rides the tool-result message alongside its FunctionResultContent.
        // The verify query never looks at that row's role, but jsonb round-tripping a second content
        // type on it must not upset the DISTINCT ON guard or the hash comparison.
        var store = await OpenAsync();
        await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.", drew: true);

        // Act
        var turns = await store.ReadSpokenTurnsAsync("C1", Token);

        // Assert
        var turn = Assert.Single(turns);
        Assert.Equal(AuditHash.OfText(turn.Spoken).Value, turn.ReplyTextSha256);
    }

    [PostgresFact]
    public async Task ReadSpokenTurnsAsync_ATurnWhoseWordsWereErased_ReturnsNoRowForIt()
    {
        // Arrange — the erasure working, and not a tamper. The chain keeps its row and goes on
        // proving what it proved.
        var store = await OpenAsync();
        await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");
        await store.EraseAsync("C1", Token);

        // Act
        var turns = await store.ReadSpokenTurnsAsync("C1", Token);

        // Assert
        Assert.Empty(turns);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    [PostgresFact]
    public async Task ReadSpokenTurnsAsync_ATurnAmendedInTheChain_ReturnsOneRowHoldingTheLatestHash()
    {
        // Arrange — a barge-in amends a turn, so the chain carries a second turn.completed for it.
        // Without the guard on the chain side the join multiplies and one turn answers twice.
        var store = await OpenAsync();
        await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");
        await store.RewriteAsync("C1", 3, new ChatMessage(ChatRole.Assistant, "Order 41 sh"), Token);
        await AmendTurnAsync("C1", turnIndex: 0, sequence: 1, amends: 0, spoken: "Order 41 sh");

        // Act
        var turns = await store.ReadSpokenTurnsAsync("C1", Token);

        // Assert
        var turn = Assert.Single(turns);
        Assert.Equal(AuditHash.OfText("Order 41 sh").Value, turn.ReplyTextSha256);
    }

    /// <summary>One ordinary turn: what the caller said, and what the caller heard.</summary>
    private static CallMessage[] Turn(string callId, int turnIndex, int ordinal) =>
    [
        new CallMessage(callId, ordinal, turnIndex, new ChatMessage(ChatRole.User, "what about order 41")),
        new CallMessage(callId, ordinal + 1, turnIndex, new ChatMessage(ChatRole.Assistant, "Order 41 ships Friday.")),
    ];

    /// <summary>Writes a tool-calling turn to store 1 and its <c>turn.completed</c> row to store 3.</summary>
    /// <remarks>
    /// The turn's two assistant messages are the point: the first announces the tool call and carries
    /// no words the caller heard, and only the second was spoken.
    /// </remarks>
    private async Task WriteToolCallingTurnAsync(
        PostgresCallStore store, string callId, int turnIndex, string spoken, bool drew = false)
    {
        List<AIContent> toolResultContents = [new FunctionResultContent("id1", "Friday")];
        if (drew)
        {
            toolResultContents.Add(new RenderContent
            {
                Name = "generative-ui",
                RenderId = "chart-1",
                Data = JsonSerializer.SerializeToElement(new { title = "Q3 revenue" }),
            });
        }

        await store.AppendAsync(
            [
                new CallMessage(callId, 0, turnIndex, new ChatMessage(ChatRole.User, "what about order 41")),
                new CallMessage(callId, 1, turnIndex, new ChatMessage(
                    ChatRole.Assistant, [new FunctionCallContent("id1", "lookup", null)])),
                new CallMessage(callId, 2, turnIndex, new ChatMessage(ChatRole.Tool, toolResultContents)),
                new CallMessage(callId, 3, turnIndex, new ChatMessage(ChatRole.Assistant, spoken)),
            ],
            Token);

        // Not disposed: the sink would take the test's own pool with it.
        PostgresAuditSink chain = new(DataSource);
        await chain.AppendAsync(
            new AuditEvent
            {
                CallId = callId,
                Sequence = 0,
                Kind = AuditEventKind.TurnCompleted,
                OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 1, TimeSpan.Zero),
                TurnIndex = turnIndex,
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(spoken).Value,
                },
            },
            Token);
    }

    /// <summary>Writes a second <c>turn.completed</c> that corrects the first, as a barge-in does.</summary>
    private async Task AmendTurnAsync(string callId, int turnIndex, long sequence, long amends, string spoken)
    {
        // Not disposed: the sink would take the test's own pool with it.
        PostgresAuditSink chain = new(DataSource);
        await chain.AppendAsync(
            new AuditEvent
            {
                CallId = callId,
                Sequence = sequence,
                Kind = AuditEventKind.TurnCompleted,
                OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 2, TimeSpan.Zero),
                TurnIndex = turnIndex,
                AmendsSequence = amends,
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(spoken).Value,
                },
            },
            Token);
    }

    /// <summary>Moves one call's rows back in time, so the sweep can be asked about them.</summary>
    private Task AgeAsync(string callId, TimeSpan age) => ExecuteAsync(
        $"UPDATE call_message SET updated_at = now() - interval '{age.TotalDays} days' WHERE call_id = '{callId}'");
}

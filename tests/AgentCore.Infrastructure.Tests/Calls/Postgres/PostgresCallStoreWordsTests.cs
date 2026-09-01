using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Calls;
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
public sealed class PostgresCallStoreWordsTests : PostgresDatabaseTest
{
    private static readonly TimeSpan NinetyDays = TimeSpan.FromDays(90);

    /// <summary>Opens the store with the call rows these tests write words against already made.</summary>
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
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);

        // Assert
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
    }

    [PostgresFact]
    public async Task AppendAsync_ATurn_LiftsTheRoleOutOfTheContent()
    {
        // Arrange
        var store = await OpenAsync();

        // Act
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);

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
                new CallMessage("C1", 0, 0, announced, "m0"),
                new CallMessage("C1", 1, 0, result, "m1"),
            ],
            cancellationToken: Token);

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
        await store.AppendAsync([], cancellationToken: Token);

        // Assert
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
    }

    [PostgresFact]
    public async Task AppendAsync_AnOrdinalTheCallAlreadyUsed_IsRefused()
    {
        // Arrange — an ordinal is permanent, so a repeat is a defect and never a silent overwrite.
        // AgentCoreChatHistoryProvider is what catches this and lets the call continue.
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);

        // Act
        var failure = await Record.ExceptionAsync(
            () => store.AppendAsync(Turn("C1", turnIndex: 1, ordinal: 0), cancellationToken: Token).AsTask());

        // Assert
        Assert.NotNull(failure);
    }

    [PostgresFact]
    public async Task ItWritesTheStateBesideTheWords()
    {
        var store = await OpenAsync();

        CallSessionState state = new()
        {
            Stage = "collecting",
            Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["model"] = JsonValue.Create("F63"),
            },
        };

        await store.AppendAsync(
            [new CallMessage("C1", 0, 0, new ChatMessage(ChatRole.User, "hello"), "m0")],
            state,
            Token);

        var record = await store.GetAsync("C1", Token);

        Assert.NotNull(record?.State);
        Assert.Equal("collecting", record.State.Stage);
        Assert.Equal("F63", record.State.Slots["model"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task ACallWithNoStateReadsAsNull()
    {
        PostgresCallStore store = new(DataSource);

        var record = await store.CreateAsync("C3", Token);

        Assert.Null(record.State);
    }

    [PostgresFact]
    public async Task AnAppendWithNoStateLeavesTheStoredStateAlone()
    {
        var store = await OpenAsync();

        await store.AppendAsync(
            [new CallMessage("C1", 0, 0, new ChatMessage(ChatRole.User, "hello"), "m0")],
            new CallSessionState { Stage = "collecting" },
            Token);
        await store.AppendAsync(
            [new CallMessage("C1", 1, 0, new ChatMessage(ChatRole.Assistant, "hi"), "m1")],
            state: null,
            Token);

        var record = await store.GetAsync("C1", Token);

        Assert.Equal("collecting", record?.State?.Stage);
    }

    [PostgresFact]
    public async Task AnAppendThatFails_LandsNeitherTheWordsNorTheState()
    {
        // Arrange — D5's whole claim: the blob rides the turn's own batch, so the words and the state
        // are of one moment and cannot disagree. Nothing else tests it, and what it rests on is
        // implicit: AppendAsync opens no explicit transaction, and atomicity comes from Npgsql
        // sending the batch between two Sync messages, which makes PostgreSQL wrap it in one
        // implicit transaction of its own. That is a property of the driver, not of this code, so it
        // is worth a test that fails the day a version of Npgsql splits the batch.
        var store = await OpenAsync();
        await store.AppendAsync(
            [new CallMessage("C1", 0, 0, new ChatMessage(ChatRole.User, "hello"), "m0")],
            new CallSessionState { Stage = "a" },
            Token);

        // Act — a batch whose FIRST row is fine and whose second repeats ordinal 0. Ordinal 2 has to
        // succeed and then be taken back for the implicit transaction to have shown itself; failing
        // the only row would prove nothing, because every command behind it never runs.
        var failure = await Record.ExceptionAsync(
            () => store.AppendAsync(
                [
                    new CallMessage("C1", 2, 1, new ChatMessage(ChatRole.User, "lands first"), "m2"),
                    new CallMessage("C1", 0, 1, new ChatMessage(ChatRole.User, "again"), "m0-again"),
                ],
                new CallSessionState { Stage = "b" },
                Token).AsTask());

        // Assert — the throw, then the row that had already succeeded, then the state the failed
        // batch tried to write. A stage of "b" beside one message would be a call whose blob had
        // moved on without its words; a surviving ordinal 2 would be a batch that was never one
        // transaction.
        Assert.NotNull(failure);
        var record = await store.GetAsync("C1", Token);
        Assert.Equal("a", record?.State?.Stage);

        var rows = await store.ReadAsync("C1", Token);
        Assert.Single(rows);
        Assert.Equal(0, rows[0].Ordinal);
    }

    [PostgresFact]
    public async Task AStateBlobThatWillNotParse_ReadsAsNoBlobRatherThanClosingTheCall()
    {
        // Arrange — jsonb refuses malformed JSON, so a bad blob is well-formed JSON of the wrong
        // shape: an older or newer build's column, or a hand-edited row. Version cannot save this
        // one, because Version is only readable after the deserialize has already succeeded.
        var store = await OpenAsync();
        await ExecuteAsync("UPDATE call SET state = '[1, 2, 3]'::jsonb WHERE call_id = 'C1'");

        // Act
        var record = await store.GetAsync("C1", Token);

        // Assert — no throw, and no state. A throw here would escape GetAsync, CreateAsync and
        // OpenSessionAsync, and the call could never be opened again by anyone: one bad row would
        // refuse every turn of that call forever, with no diagnostic and no way back.
        Assert.NotNull(record);
        Assert.Null(record.State);
    }

    // ---------------------------------------------------------------------------------------------
    // Read. It runs at call start, on a resume, and nowhere else.
    // ---------------------------------------------------------------------------------------------
    [PostgresFact]
    public async Task ReadAsync_AWrittenCall_ReturnsEveryRowInOrdinalOrder()
    {
        // Arrange
        var store = await OpenAsync();
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);
        await store.AppendAsync(Turn("C1", turnIndex: 1, ordinal: 2), cancellationToken: Token);

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
        await store.AppendAsync(Turn("C1", turnIndex: 3, ordinal: 6), cancellationToken: Token);

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
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);

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
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);

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
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);
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
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);

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
        await store.AppendAsync(Turn("C1", turnIndex: 0, ordinal: 0), cancellationToken: Token);
        await store.AppendAsync(Turn("C2", turnIndex: 0, ordinal: 0), cancellationToken: Token);

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
        var firstEventId = await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");
        await store.RewriteAsync("C1", 3, new ChatMessage(ChatRole.Assistant, "Order 41 sh"), Token);
        await AmendTurnAsync("C1", turnIndex: 0, amends: firstEventId, spoken: "Order 41 sh");

        // Act
        var turns = await store.ReadSpokenTurnsAsync("C1", Token);

        // Assert
        var turn = Assert.Single(turns);
        Assert.Equal(AuditHash.OfText("Order 41 sh").Value, turn.ReplyTextSha256);
    }

    /// <summary>One ordinary turn: what the caller said, and what the caller heard.</summary>
    private static CallMessage[] Turn(string callId, int turnIndex, int ordinal) =>
    [
        new CallMessage(callId, ordinal, turnIndex, new ChatMessage(ChatRole.User, "what about order 41"), $"m{ordinal}"),
        new CallMessage(callId, ordinal + 1, turnIndex, new ChatMessage(ChatRole.Assistant, "Order 41 ships Friday."), $"m{ordinal + 1}"),
    ];

    /// <summary>Writes a tool-calling turn to store 1 and its <c>turn.completed</c> row to store 3.</summary>
    private async Task<Guid> WriteToolCallingTurnAsync(
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
                new CallMessage(callId, 0, turnIndex, new ChatMessage(ChatRole.User, "what about order 41"), "m0"),
                new CallMessage(callId, 1, turnIndex, new ChatMessage(
                    ChatRole.Assistant, [new FunctionCallContent("id1", "lookup", null)]), "m1"),
                new CallMessage(callId, 2, turnIndex, new ChatMessage(ChatRole.Tool, toolResultContents), "m2"),
                new CallMessage(callId, 3, turnIndex, new ChatMessage(ChatRole.Assistant, spoken), "m3"),
            ],
            cancellationToken: Token);

        // Not disposed: the sink would take the test's own pool with it.
        Guid eventId = Guid.CreateVersion7();
        PostgresAuditSink chain = new(DataSource);
        await chain.AppendAsync(
            new AuditEvent
            {
                CallId = callId,
                EventId = eventId,
                Kind = AuditEventKind.TurnCompleted,
                OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 1, TimeSpan.Zero),
                TurnIndex = turnIndex,
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(spoken).Value,
                },
            },
            Token);

        return eventId;
    }

    /// <summary>Writes a second <c>turn.completed</c> that corrects the first, as a barge-in does.</summary>
    private async Task AmendTurnAsync(string callId, int turnIndex, Guid amends, string spoken)
    {
        // Not disposed: the sink would take the test's own pool with it.
        PostgresAuditSink chain = new(DataSource);
        await chain.AppendAsync(
            new AuditEvent
            {
                CallId = callId,
                EventId = Guid.CreateVersion7(),
                Kind = AuditEventKind.TurnCompleted,
                OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 2, TimeSpan.Zero),
                TurnIndex = turnIndex,
                AmendsEventId = amends,
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

using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Audit.Postgres;

/// <summary>
/// Store 3 in PostgreSQL.
/// </summary>
/// <remarks>
/// These need a live PostgreSQL and skip without one — <see cref="PostgresFactAttribute"/> names the
/// variable. Each test takes a database of its own.
/// </remarks>
public sealed class PostgresAuditSinkTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task AppendAsync_OneEvent_WritesOneRow()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);

        // Act
        await sink.AppendAsync(Started("C1"), Token);

        // Assert
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    [PostgresFact]
    public async Task AppendManyAsync_ARun_WritesEveryEventInOrder()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);
        AuditEvent[] run = [Started("C1"), TurnCompleted("C1", 0), Ended("C1")];

        // Act
        await sink.AppendManyAsync(run, Token);

        // Assert — write_position is insertion order, and it lines up with the sequence the store
        // assigned because a single-call batch is numbered in submission order.
        var sequences = await ReadSequencesAsync();
        Assert.Equal([0L, 1L, 2L], sequences);
    }

    [PostgresFact]
    public async Task AppendAsync_EventWithNoTurnOrAmendment_StoresBothAsNull()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);

        // Act
        await sink.AppendAsync(Started("C1"), Token);

        // Assert
        var nulls = await ScalarAsync<bool>("SELECT turn_index IS NULL AND amends_event_id IS NULL FROM audit_event");
        Assert.True(nulls);
    }

    [PostgresFact]
    public async Task AppendAsync_EventWithPayload_StoresItAsAJsonObject()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);

        // Act
        await sink.AppendAsync(TurnCompleted("C1", 0), Token);

        // Assert
        var reply = await ScalarAsync<string>($"SELECT payload ->> '{AuditPayloadKeys.ReplyTextSha256}' FROM audit_event");
        Assert.Equal(AuditHash.OfText("the belt ships Friday").Value, reply);
    }

    [PostgresFact]
    public async Task AppendManyAsync_RunHoldingAMalformedEvent_WritesNoneOfIt()
    {
        // Arrange — an ending with no reason is unreadable a year later, so it is refused before any
        // row of the run is written rather than after the first two landed.
        PostgresAuditSink sink = new(DataSource);
        AuditEvent reasonless = Ended("C1") with { Payload = new Dictionary<string, string>(StringComparer.Ordinal) };
        AuditEvent[] run = [Started("C1"), TurnCompleted("C1", 0), reasonless];

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => sink.AppendManyAsync(run, Token).AsTask());

        // Assert
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    [PostgresFact]
    public async Task AppendAsync_ConcurrentWritersOnSeparateCalls_WriteEveryRow()
    {
        // Arrange — four hosts on one table. Nothing serialises them: there is no head to claim.
        var connectionString = Database.ConnectionString;
        var writers = Enumerable.Range(0, 4)
            .Select(writer => Task.Run(
                async () =>
                {
                    PostgresAuditSink sink = new(NpgsqlDataSource.Create(connectionString));
                    await using (sink)
                    {
                        foreach (var _ in Enumerable.Range(0, 5))
                        {
                            await sink.AppendAsync(Started($"C{writer}"), Token);
                        }
                    }
                },
                Token));

        // Act
        await Task.WhenAll(writers);

        // Assert
        Assert.Equal(20L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    [PostgresFact]
    public async Task TheStoreNumbersTheChainFromZero()
    {
        // Every event below shares the same OccurredAt, and EventId is a random-within-a-millisecond
        // UUID v7 — so the only signal that could accidentally look like an ordering (occurred_at, or
        // event_id) is deliberately useless here. Asserting kind alongside sequence is what would
        // catch a regression to ORDER BY d.event_id: that still leaves every sequence dense from
        // zero, but scrambles which kind lands on which number, intermittently.
        PostgresAuditSink sink = new(DataSource);

        await sink.AppendManyAsync(
        [
            Event("C1", AuditEventKind.CallStarted),
            Event("C1", AuditEventKind.TurnCompleted),
            Event("C1", AuditEventKind.CallEnded),
        ], Token);

        var sequences = await SequencesOfAsync("C1");
        var kinds = await KindsOfAsync("C1");
        Assert.Equal([0L, 1L, 2L], sequences);
        Assert.Equal(["call.started", "turn.completed", "call.ended"], kinds);
    }

    [PostgresFact]
    public async Task ASecondBatchContinuesTheChain()
    {
        PostgresAuditSink sink = new(DataSource);

        await sink.AppendManyAsync([Event("C1", AuditEventKind.CallStarted)], Token);
        await sink.AppendManyAsync([Event("C1", AuditEventKind.TurnCompleted)], Token);

        var sequences = await SequencesOfAsync("C1");
        Assert.Equal([0L, 1L], sequences);
    }

    [PostgresFact]
    public async Task OneBatchNumbersTwoCallsIndependently()
    {
        PostgresAuditSink sink = new(DataSource);

        await sink.AppendManyAsync(
        [
            Event("C1", AuditEventKind.CallStarted),
            Event("C2", AuditEventKind.CallStarted),
            Event("C1", AuditEventKind.TurnCompleted),
        ], Token);

        var c1Sequences = await SequencesOfAsync("C1");
        var c2Sequences = await SequencesOfAsync("C2");
        Assert.Equal([0L, 1L], c1Sequences);
        Assert.Equal([0L], c2Sequences);
    }

    [PostgresFact]
    public async Task AReplayedBatchWritesNothingTwice()
    {
        PostgresAuditSink sink = new(DataSource);
        AuditEvent[] run = [Event("C1", AuditEventKind.CallStarted)];

        await sink.AppendManyAsync(run, Token);
        await sink.AppendManyAsync(run, Token);

        var sequences = await SequencesOfAsync("C1");
        Assert.Equal([0L], sequences);
    }

    [PostgresFact]
    public async Task APartiallyReplayedBatchLeavesNoGap()
    {
        // Arrange — C1 already holds a and b at 0 and 1. The second batch replays both and adds c.
        // row_number() must never count a and b: they survive nowhere (ON CONFLICT drops them), so
        // numbering them would reserve a sequence — here, 2 and 3 — that no row ever lands on, and c
        // would land at 4 instead of 2. The NOT EXISTS filter is what keeps this dense.
        PostgresAuditSink sink = new(DataSource);
        AuditEvent a = Event("C1", AuditEventKind.CallStarted);
        AuditEvent b = Event("C1", AuditEventKind.TurnCompleted);
        AuditEvent c = Event("C1", AuditEventKind.CallEnded);

        await sink.AppendManyAsync([a, b], Token);

        // Act
        await sink.AppendManyAsync([a, b, c], Token);

        // Assert
        var sequences = await SequencesOfAsync("C1");
        Assert.Equal([0L, 1L, 2L], sequences);
    }

    [PostgresFact]
    public async Task ConcurrentWritersOnTheSameCall_NumberDenselyWithNoGapOrDuplicate()
    {
        // Arrange — one call id, several writers. Without the advisory lock two writers can both
        // read the same max(sequence) and both compute the same numbers; one of them then loses a
        // unique-violation race that QueuedAuditSink would swallow as a dropped batch. The lock is
        // what makes that race impossible instead of merely unlikely.
        const int writerCount = 4;
        const int eventsPerWriter = 5;
        var connectionString = Database.ConnectionString;

        var writers = Enumerable.Range(0, writerCount)
            .Select(_ => Task.Run(
                async () =>
                {
                    PostgresAuditSink sink = new(NpgsqlDataSource.Create(connectionString));
                    await using (sink)
                    {
                        foreach (var _ in Enumerable.Range(0, eventsPerWriter))
                        {
                            await sink.AppendAsync(Event("C1", AuditEventKind.TurnCompleted), Token);
                        }
                    }
                },
                Token));

        // Act
        await Task.WhenAll(writers);

        // Assert — dense from zero with no gap and no duplicate. A gap or a short count means a
        // writer's row was silently dropped; a duplicate cannot reach the table at all, because
        // audit_event_call_sequence_unique refuses it, so either failure mode shows up here as the
        // read sequences failing to equal the full contiguous range.
        var sequences = await SequencesOfAsync("C1");
        Assert.Equal(
            Enumerable.Range(0, writerCount * eventsPerWriter).Select(i => (long)i),
            sequences);
    }

    private static AuditEvent Started(string callId) => new()
    {
        CallId = callId,
        EventId = Guid.CreateVersion7(),
        Kind = AuditEventKind.CallStarted,
        OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero),
    };

    private static AuditEvent TurnCompleted(string callId, int turnIndex) => new()
    {
        CallId = callId,
        EventId = Guid.CreateVersion7(),
        Kind = AuditEventKind.TurnCompleted,
        OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 1, TimeSpan.Zero),
        TurnIndex = turnIndex,
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText("the belt ships Friday").Value,
        },
    };

    private static AuditEvent Ended(string callId) => new()
    {
        CallId = callId,
        EventId = Guid.CreateVersion7(),
        Kind = AuditEventKind.CallEnded,
        OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 2, TimeSpan.Zero),
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.EndReason] = CallEndReasons.ToToken(CallEndReason.CallerHungUp),
        },
    };

    private static AuditEvent Event(string callId, AuditEventKind kind) => new()
    {
        CallId = callId,
        EventId = Guid.CreateVersion7(),
        Kind = kind,
        OccurredAt = DateTimeOffset.UnixEpoch,
        Payload = kind == AuditEventKind.CallEnded
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.EndReason] = CallEndReasons.ToToken(CallEndReason.AgentCompleted),
            }
            : new Dictionary<string, string>(StringComparer.Ordinal),
    };

    /// <summary>Reads the sequences back in the order the table wrote them.</summary>
    private async Task<List<long>> ReadSequencesAsync()
    {
        List<long> sequences = [];

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "SELECT sequence FROM audit_event ORDER BY write_position");
        await using var reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            sequences.Add(reader.GetInt64(0));
        }

        return sequences;
    }

    private async Task<long[]> SequencesOfAsync(string callId)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT sequence FROM audit_event WHERE call_id = $1 ORDER BY sequence");
        command.Parameters.Add(new NpgsqlParameter { Value = callId });

        List<long> sequences = [];
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            sequences.Add(reader.GetInt64(0));
        }

        return [.. sequences];
    }

    /// <summary>Reads the kinds back in sequence order, so a test can pin which fact landed where.</summary>
    private async Task<string[]> KindsOfAsync(string callId)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT kind FROM audit_event WHERE call_id = $1 ORDER BY sequence");
        command.Parameters.Add(new NpgsqlParameter { Value = callId });

        List<string> kinds = [];
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            kinds.Add(reader.GetString(0));
        }

        return [.. kinds];
    }
}

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
        await sink.AppendAsync(Started("C1", 0), Token);

        // Assert
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    [PostgresFact]
    public async Task AppendManyAsync_ARun_WritesEveryEventInOrder()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);
        AuditEvent[] run = [Started("C1", 0), TurnCompleted("C1", 1, 0), Ended("C1", 2)];

        // Act
        await sink.AppendManyAsync(run, Token);

        // Assert
        var sequences = await ReadSequencesAsync();
        Assert.Equal(run.Select(e => e.Sequence), sequences);
    }

    [PostgresFact]
    public async Task AppendAsync_EventWithNoTurnOrAmendment_StoresBothAsNull()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);

        // Act
        await sink.AppendAsync(Started("C1", 0), Token);

        // Assert
        var nulls = await ScalarAsync<bool>("SELECT turn_index IS NULL AND amends_sequence IS NULL FROM audit_event");
        Assert.True(nulls);
    }

    [PostgresFact]
    public async Task AppendAsync_EventWithPayload_StoresItAsAJsonObject()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);

        // Act
        await sink.AppendAsync(TurnCompleted("C1", 0, 0), Token);

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
        AuditEvent reasonless = Ended("C1", 2) with { Payload = new Dictionary<string, string>(StringComparer.Ordinal) };
        AuditEvent[] run = [Started("C1", 0), TurnCompleted("C1", 1, 0), reasonless];

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => sink.AppendManyAsync(run, Token).AsTask());

        // Assert
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    [PostgresFact]
    public async Task AppendAsync_SameCallAndSequenceTwice_IsRefused()
    {
        // Arrange — the caller allocates the sequence, so a duplicate is a caller defect and the
        // table is what catches it.
        PostgresAuditSink sink = new(DataSource);
        await sink.AppendAsync(Started("C1", 0), Token);

        // Act
        var failure = await Assert.ThrowsAsync<PostgresException>(() => sink.AppendAsync(Started("C1", 0), Token).AsTask());

        // Assert
        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
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
                        foreach (var sequence in Enumerable.Range(0, 5))
                        {
                            await sink.AppendAsync(Started($"C{writer}", sequence), Token);
                        }
                    }
                },
                Token));

        // Act
        await Task.WhenAll(writers);

        // Assert
        Assert.Equal(20L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    private static AuditEvent Started(string callId, long sequence) => new()
    {
        CallId = callId,
        Sequence = sequence,
        Kind = AuditEventKind.CallStarted,
        OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero),
    };

    private static AuditEvent TurnCompleted(string callId, long sequence, int turnIndex) => new()
    {
        CallId = callId,
        Sequence = sequence,
        Kind = AuditEventKind.TurnCompleted,
        OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 1, TimeSpan.Zero),
        TurnIndex = turnIndex,
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText("the belt ships Friday").Value,
        },
    };

    private static AuditEvent Ended(string callId, long sequence) => new()
    {
        CallId = callId,
        Sequence = sequence,
        Kind = AuditEventKind.CallEnded,
        OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 2, TimeSpan.Zero),
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.EndReason] = CallEndReasons.ToToken(CallEndReason.CallerHungUp),
        },
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
}

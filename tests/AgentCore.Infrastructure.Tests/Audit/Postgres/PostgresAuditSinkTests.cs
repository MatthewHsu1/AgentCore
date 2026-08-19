using System.Text.Json;
using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Audit.Postgres;

/// <summary>
/// The D23 audit chain in PostgreSQL.
/// </summary>
/// <remarks>
/// These need a live PostgreSQL and skip without one — <see cref="PostgresFactAttribute"/> names the
/// variable. The chain's head is global to the table, so each test takes a database of its own.
/// </remarks>
public sealed class PostgresAuditSinkTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task AppendAsync_FirstEvent_LinksToGenesis()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);

        // Act
        await sink.AppendAsync(Started("C1", 0), Token);

        // Assert
        var links = await ReadChainAsync();
        Assert.Equal(AuditHash.Genesis, Assert.Single(links).PreviousHash);
    }

    [PostgresFact]
    public async Task AppendAsync_SeveralEvents_ChainVerifies()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);
        await sink.AppendAsync(Started("C1", 0), Token);
        await sink.AppendAsync(TurnCompleted("C1", 1, 0), Token);

        // Act
        await sink.AppendAsync(Ended("C1", 2), Token);

        // Assert
        Assert.True(AuditChain.Verify(await ReadChainAsync()).IsIntact);
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
        var links = await ReadChainAsync();
        Assert.Equal(run.Select(e => e.Sequence), links.Select(l => l.Event.Sequence));
    }

    [PostgresFact]
    public async Task AppendManyAsync_ARun_ChainVerifies()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);
        AuditEvent[] run = [Started("C1", 0), TurnCompleted("C1", 1, 0), Ended("C1", 2)];

        // Act
        await sink.AppendManyAsync(run, Token);

        // Assert
        Assert.True(AuditChain.Verify(await ReadChainAsync()).IsIntact);
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
    public async Task AppendAsync_ConcurrentWriters_ProduceOneUnbrokenChain()
    {
        // Arrange — four sinks on one table, which is four hosts on one chain.
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
        var links = await ReadChainAsync();
        Assert.Equal(20, links.Count);
        Assert.True(AuditChain.Verify(links).IsIntact);
    }

    [PostgresFact]
    public async Task AppendAsync_AnotherWriterTookTheHeadFirst_RetriesAndKeepsTheChainWhole()
    {
        // Arrange — an uncommitted row already claims the head this append is about to claim. The
        // insert blocks on audit_event_previous_hash_unique until that transaction commits, and then
        // it is a 23505 that only a re-read of the head can get past.
        PostgresAuditSink sink = new(DataSource);
        await sink.AppendAsync(Started("C1", 0), Token);
        var head = await ScalarAsync<string>("SELECT hash FROM audit_event ORDER BY chain_position DESC LIMIT 1");

        await using var interloper = await DataSource.OpenConnectionAsync(Token);
        await using var claim = await interloper.BeginTransactionAsync(Token);
        AuditEvent stolen = Started("C9", 0);
        await using (NpgsqlCommand steal = new(
            """
            INSERT INTO audit_event (call_id, sequence, kind, occurred_at, previous_hash, hash)
            VALUES ($1, $2, 'call.started', $3, $4, $5)
            """,
            interloper,
            claim))
        {
            steal.Parameters.Add(new NpgsqlParameter { Value = stolen.CallId });
            steal.Parameters.Add(new NpgsqlParameter { Value = stolen.Sequence });
            steal.Parameters.Add(new NpgsqlParameter { Value = stolen.OccurredAt });
            steal.Parameters.Add(new NpgsqlParameter { Value = head });
            steal.Parameters.Add(new NpgsqlParameter { Value = AuditChain.ComputeHash(stolen, AuditHash.Parse(head)).Value });
            await steal.ExecuteNonQueryAsync(Token);
        }

        var blocked = Task.Run(() => sink.AppendAsync(TurnCompleted("C1", 1, 0), Token).AsTask(), Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250), Token);

        // Act
        await claim.CommitAsync(Token);
        await blocked;

        // Assert
        var links = await ReadChainAsync();
        Assert.Equal(3, links.Count);
        Assert.True(AuditChain.Verify(links).IsIntact);
    }

    [PostgresFact]
    public async Task Append_UnlinkedPreviousHash_IsRejected()
    {
        // Arrange — the fork the append statement exists to prevent. The row names a predecessor the
        // table does not hold, which no constraint refuses: UNIQUE (previous_hash) stops two rows
        // claiming one predecessor, not one row claiming a predecessor that never existed.
        PostgresAuditSink sink = new(DataSource);
        await sink.AppendAsync(Started("C1", 0), Token);

        // Act
        var written = await AppendWithClaimedHeadAsync(TurnCompleted("C1", 1, 0), AuditHash.OfText("a head nothing wrote"));

        // Assert
        Assert.Equal(0, written);
    }

    [PostgresFact]
    public async Task Append_UnlinkedPreviousHash_LeavesTheChainWhole()
    {
        // Arrange
        PostgresAuditSink sink = new(DataSource);
        await sink.AppendAsync(Started("C1", 0), Token);

        // Act
        await AppendWithClaimedHeadAsync(TurnCompleted("C1", 1, 0), AuditHash.OfText("a head nothing wrote"));

        // Assert
        Assert.True(AuditChain.Verify(await ReadChainAsync()).IsIntact);
    }

    [PostgresFact]
    public async Task Verify_RowEditedBehindTheTriggers_ReportsABrokenLink()
    {
        // Arrange — session_replication_role bypasses all three triggers in one statement, which is
        // the hole the external anchor and this check exist to cover.
        PostgresAuditSink sink = new(DataSource);
        await sink.AppendManyAsync([Started("C1", 0), TurnCompleted("C1", 1, 0)], Token);

        // Act
        await ExecuteAsync(
            """
            SET session_replication_role = replica;
            UPDATE audit_event SET kind = 'call.ended' WHERE sequence = 0;
            SET session_replication_role = origin;
            """);

        // Assert
        Assert.False(AuditChain.Verify(await ReadChainAsync()).IsIntact);
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

    /// <summary>
    /// Runs the sink's own append statement while claiming a head of the caller's choosing.
    /// </summary>
    /// <returns>The number of rows it wrote.</returns>
    /// <remarks>
    /// The statement is the subject here, so the test names it rather than reaching the sink through
    /// a race it cannot schedule. A claimed head that is not the table's head is exactly the state a
    /// second writer creates between the sink's read and its insert.
    /// </remarks>
    private async Task<int> AppendWithClaimedHeadAsync(AuditEvent auditEvent, AuditHash claimedHead)
    {
        await using var command = DataSource.CreateCommand(PostgresAuditSink.AppendSql);

        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.CallId });
        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.Sequence });
        command.Parameters.Add(new NpgsqlParameter { Value = AuditEventKinds.ToToken(auditEvent.Kind) });
        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.OccurredAt });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)auditEvent.TurnIndex ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = "{}", NpgsqlDbType = NpgsqlDbType.Jsonb });
        command.Parameters.Add(new NpgsqlParameter { Value = AuditChain.ComputeHash(auditEvent, claimedHead).Value });
        command.Parameters.Add(new NpgsqlParameter { Value = claimedHead.Value });

        return await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Reads the whole table back as links, which is what <c>chain_check</c> will do.</summary>
    private async Task<IReadOnlyList<AuditChainLink>> ReadChainAsync()
    {
        List<AuditChainLink> links = [];

        await using NpgsqlCommand command = DataSource.CreateCommand(
            """
            SELECT call_id, sequence, kind, occurred_at, turn_index, amends_sequence,
                   payload, previous_hash, hash
            FROM audit_event
            ORDER BY chain_position
            """);
        await using var reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            Assert.True(AuditEventKinds.TryParse(reader.GetString(2), out AuditEventKind kind));

            links.Add(new AuditChainLink
            {
                Event = new AuditEvent
                {
                    CallId = reader.GetString(0),
                    Sequence = reader.GetInt64(1),
                    Kind = kind,
                    OccurredAt = reader.GetFieldValue<DateTimeOffset>(3),
                    TurnIndex = await reader.IsDBNullAsync(4, Token) ? null : reader.GetInt32(4),
                    AmendsSequence = await reader.IsDBNullAsync(5, Token) ? null : reader.GetInt64(5),
                    Payload = Payload(reader.GetString(6)),
                },
                PreviousHash = AuditHash.Parse(reader.GetString(7)),
                Hash = AuditHash.Parse(reader.GetString(8)),
            });
        }

        return links;
    }

    private static Dictionary<string, string> Payload(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

}

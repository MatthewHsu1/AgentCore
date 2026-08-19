using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Npgsql;
using NpgsqlTypes;

namespace AgentCore.Infrastructure.Audit.Postgres;

/// <summary>
/// The audit chain, in PostgreSQL. It appends and it never updates.
/// </summary>
/// <remarks>
/// <para>
/// This is the raw store, so it blocks on the database. <see cref="Application.Audit.QueuedAuditSink"/>
/// is what keeps that off the turn, and the composition root is what puts one in front of the other.
/// </para>
/// <para>
/// <b>Who allocates what.</b> The caller allocates <see cref="AuditEvent.Sequence"/>. The database
/// allocates <c>chain_position</c> — <c>GENERATED ALWAYS AS IDENTITY</c> refuses a supplied value —
/// and the database supplies <c>previous_hash</c> from its own head. This adapter only computes the
/// event's own hash, which it cannot do without reading that same head first.
/// </para>
/// <para>
/// <b>chain_position is an ordering and never a count.</b> A failed insert burns an identity value,
/// so retries leave gaps. A gap is not a break; only <c>previous_hash</c> decides that.
/// </para>
/// </remarks>
internal sealed class PostgresAuditSink : IAuditSinkPort, IAsyncDisposable
{
    /// <summary>
    /// How many times an append re-reads the head before it gives up.
    /// </summary>
    private const int MaxAttempts = 5;

    private const string ReadHeadSql = "SELECT hash FROM audit_event ORDER BY chain_position DESC LIMIT 1";

    /// <summary>
    /// Serialises appends across every process that writes this chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rows cannot both follow one head, so appends to a hash chain do not run in parallel — they
    /// only fail in parallel. Without this lock four writers spend their attempts invalidating each
    /// other: measured, four writers appending five events each exhausted five attempts every time and
    /// wrote nothing. With it, each waits its turn and the retry below becomes the rare path.
    /// </para>
    /// <para>
    /// It costs microseconds against a durable insert's 13 ms, and it is held for the transaction, so
    /// it is released by the commit or the rollback and never outlives a crashed writer. It is
    /// transaction-scoped and not session-scoped for that reason.
    /// </para>
    /// </remarks>
    private const long AppendLockKey = 0x41C05CE700000002;

    /// <summary>
    /// Appends one event under the database's own head, and refuses if that head is not the one this
    /// process hashed over.
    /// </summary>
    internal const string AppendSql = """
        WITH head AS (
            SELECT hash FROM audit_event ORDER BY chain_position DESC LIMIT 1
        )
        INSERT INTO audit_event (
            call_id, sequence, kind, occurred_at, turn_index, amends_sequence, payload,
            previous_hash, hash)
        SELECT $1, $2, $3, $4, $5, $6, $7,
               coalesce(head.hash, repeat('0', 64)),
               $8
        FROM (SELECT 1) AS one LEFT JOIN head ON true
        WHERE coalesce(head.hash, repeat('0', 64)) = $9
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the sink over a data source it then owns.</summary>
    /// <param name="dataSource">The pool every append runs on. Disposing the sink disposes it.</param>
    /// <exception cref="ArgumentNullException">The data source is <see langword="null"/>.</exception>
    /// <remarks>
    /// The sink owns the pool because nothing above it does: the composition root registers the store
    /// it was handed and disposes that, and a pool left open outlives the host it belonged to.
    /// </remarks>
    public PostgresAuditSink(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return AppendManyAsync([auditEvent], cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask AppendManyAsync(
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvents);

        if (auditEvents.Count == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                if (await TryAppendAsync(auditEvents, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Another writer claimed the head between the read and the insert. This is not a
                // serialization failure, so nothing retries it underneath us, and re-issuing the same
                // statement would fail the same way: the next attempt re-reads the head first.
            }
        }

        throw new InvalidOperationException(
            $"The audit chain head moved under all {MaxAttempts} attempts to append {auditEvents.Count} event(s). Nothing was written.");
    }

    /// <summary>
    /// Disposes the pool this sink was given.
    /// </summary>
    /// <returns>A task that completes when the pool is closed.</returns>
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    /// <summary>
    /// Reads the head, hashes the run over it, and writes the run under it.
    /// </summary>
    /// <returns><see langword="false"/> when the head moved and nothing was written.</returns>
    private async Task<bool> TryAppendAsync(
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand serialise = new("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            serialise.Parameters.Add(new NpgsqlParameter { Value = AppendLockKey });

            await serialise.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        AuditHash head = await ReadHeadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await using NpgsqlBatch batch = new(connection, transaction);

        AuditHash previous = head;

        foreach (AuditEvent auditEvent in auditEvents)
        {
            AuditHash hash = AuditChain.ComputeHash(auditEvent, previous);
            batch.BatchCommands.Add(AppendCommand(auditEvent, hash, previous));
            previous = hash;
        }

        var written = await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (written != auditEvents.Count)
        {
            // A guard refused, so the head is not what this attempt hashed over. The transaction is
            // rolled back by disposal and every row of the run goes with it.
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static async Task<AuditHash> ReadHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ReadHeadSql, connection, transaction);
        var head = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return head is string value ? AuditHash.Parse(value) : AuditHash.Genesis;
    }

    private static NpgsqlBatchCommand AppendCommand(AuditEvent auditEvent, AuditHash hash, AuditHash previous)
    {
        NpgsqlBatchCommand command = new(AppendSql);

        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.CallId });
        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.Sequence });
        command.Parameters.Add(new NpgsqlParameter { Value = AuditEventKinds.ToToken(auditEvent.Kind) });
        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.OccurredAt });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)auditEvent.TurnIndex ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)auditEvent.AmendsSequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = PayloadJson(auditEvent.Payload), NpgsqlDbType = NpgsqlDbType.Jsonb });
        command.Parameters.Add(new NpgsqlParameter { Value = hash.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = previous.Value });

        return command;
    }

    /// <summary>
    /// Writes the payload as a flat JSON object of strings.
    /// </summary>
    private static string PayloadJson(IReadOnlyDictionary<string, string> payload)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> entry in payload)
            {
                writer.WriteString(entry.Key, entry.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

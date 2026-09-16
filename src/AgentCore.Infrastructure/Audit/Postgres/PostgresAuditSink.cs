using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace AgentCore.Infrastructure.Audit.Postgres;

/// <summary>
/// Store 3, in PostgreSQL. It appends and it never updates.
/// </summary>
internal sealed class PostgresAuditSink : IAuditSinkPort, IAsyncDisposable
{
    private const string Schema = PostgresSchema.SchemaName;

    /// <summary>Serialises the writers of one call so two of them cannot pick the same sequence.</summary>
    internal const string LockSql = "SELECT pg_advisory_xact_lock(hashtext($1))";

    /// <summary>Appends one call's run, numbering only the rows that will actually survive.</summary>
    internal const string AppendSql = $"""
        INSERT INTO {Schema}.audit_event (
            call_id, event_id, sequence, kind, occurred_at, turn_index, amends_event_id, payload)
        SELECT $1,
               d.event_id,
               coalesce((SELECT max(sequence) FROM {Schema}.audit_event WHERE call_id = $1), -1)
                   + row_number() OVER (ORDER BY d.position),
               d.kind, d.occurred_at, d.turn_index, d.amends_event_id, d.payload
          FROM unnest($2::uuid[], $3::text[], $4::timestamptz[],
                      $5::int[], $6::uuid[], $7::jsonb[])
               WITH ORDINALITY
               AS d(event_id, kind, occurred_at, turn_index, amends_event_id, payload, position)
         WHERE NOT EXISTS (
             SELECT 1 FROM {Schema}.audit_event e WHERE e.call_id = $1 AND e.event_id = d.event_id)
        ON CONFLICT (call_id, event_id) DO NOTHING
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the sink over a data source it then owns.</summary>
    /// <param name="dataSource">The pool every append runs on. Disposing the sink disposes it.</param>
    /// <exception cref="ArgumentNullException">The data source is <see langword="null"/>.</exception>
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

        // Every event is checked before any of them is written, so a run that holds one malformed
        // event writes none of it and the caller learns which rule it broke.
        foreach (AuditEvent auditEvent in auditEvents)
        {
            AuditEventVocabulary.Validate(auditEvent);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlBatch batch = new(connection, transaction);

        // Grouped, because the queue in front of this sink batches across calls, and a call is the
        // unit the sequence counts within. Ordered by call id, because two transactions that each
        // hold two calls would otherwise be able to take the two locks in opposite orders and wait
        // on each other forever. Best effort, and only that: the lock is on hashtext(call_id), so a
        // consistent order on ids is a consistent order on locks only while the hash is injective
        // over the ids in play. See the remarks on LockSql for what a collision costs.
        foreach (var call in auditEvents
            .GroupBy(item => item.CallId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            NpgsqlBatchCommand lockCommand = new(LockSql);
            lockCommand.Parameters.Add(new NpgsqlParameter { Value = call.Key });
            batch.BatchCommands.Add(lockCommand);

            batch.BatchCommands.Add(AppendCommand(call.Key, [.. call]));
        }

        // One round trip for the run. A durable insert is ~13 ms, so twenty apart cost 260 ms and
        // twenty together cost 13 ms.
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the pool this sink was given.
    /// </summary>
    /// <returns>A task that completes when the pool is closed.</returns>
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    [SuppressMessage(
        "csharpsquid",
        "S3265",
        Justification = "Npgsql documents Array combined with an element type via bit OR, and the enum omits Flags only upstream.")]
    private static NpgsqlBatchCommand AppendCommand(string callId, IReadOnlyList<AuditEvent> run)
    {
        NpgsqlBatchCommand command = new(AppendSql);

        // Two events in the same batch sharing an EventId would both survive the NOT EXISTS filter
        // (neither is in the table yet), so row_number() would number the duplicate too and
        // ON CONFLICT would then silently drop it — reserving a sequence for a row that never lands.
        // One survivor per EventId keeps the numbering dense.
        AuditEvent[] distinct = [.. run.DistinctBy(item => item.EventId)];

        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>
        {
            TypedValue = [.. distinct.Select(item => item.EventId)],
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>
        {
            TypedValue = [.. distinct.Select(item => AuditEventKinds.ToToken(item.Kind))],
        });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>
        {
            TypedValue = [.. distinct.Select(item => item.OccurredAt)],
        });
        command.Parameters.Add(new NpgsqlParameter<int?[]>
        {
            TypedValue = [.. distinct.Select(item => item.TurnIndex)],
        });
        command.Parameters.Add(new NpgsqlParameter<Guid?[]>
        {
            TypedValue = [.. distinct.Select(item => item.AmendsEventId)],
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = distinct.Select(item => PayloadJson(item.Payload)).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb,
        });

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

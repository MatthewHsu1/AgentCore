using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Npgsql;
using NpgsqlTypes;

namespace AgentCore.Infrastructure.Audit.Postgres;

/// <summary>
/// Store 3, in PostgreSQL. It appends and it never updates.
/// </summary>
internal sealed class PostgresAuditSink : IAuditSinkPort, IAsyncDisposable
{
    internal const string AppendSql = """
        INSERT INTO audit_event (
            call_id, sequence, kind, occurred_at, turn_index, amends_sequence, payload)
        VALUES ($1, $2, $3, $4, $5, $6, $7)
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

        foreach (AuditEvent auditEvent in auditEvents)
        {
            batch.BatchCommands.Add(AppendCommand(auditEvent));
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

    private static NpgsqlBatchCommand AppendCommand(AuditEvent auditEvent)
    {
        NpgsqlBatchCommand command = new(AppendSql);

        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.CallId });
        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.Sequence });
        command.Parameters.Add(new NpgsqlParameter { Value = AuditEventKinds.ToToken(auditEvent.Kind) });
        command.Parameters.Add(new NpgsqlParameter { Value = auditEvent.OccurredAt });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)auditEvent.TurnIndex ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)auditEvent.AmendsSequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = PayloadJson(auditEvent.Payload), NpgsqlDbType = NpgsqlDbType.Jsonb });

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

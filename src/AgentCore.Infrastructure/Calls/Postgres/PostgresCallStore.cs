using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Npgsql;
using NpgsqlTypes;
using static AgentCore.Infrastructure.Calls.Postgres.PostgresCallRows;
using static AgentCore.Infrastructure.Calls.Postgres.PostgresCallStoreSql;

namespace AgentCore.Infrastructure.Calls.Postgres;

/// <summary>The store, in PostgreSQL: a call, who may see it, and its words.</summary>
internal sealed class PostgresCallStore : ICallStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over a data source it then owns.</summary>
    /// <param name="dataSource">The pool every statement runs on. Disposing the store disposes it.</param>
    /// <exception cref="ArgumentNullException">The data source is <see langword="null"/>.</exception>
    public PostgresCallStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask<CallRecord> CreateAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using (var command = _dataSource.CreateCommand(CreateSql))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = callId });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetAsync(callId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Store 0 lost call '{callId}' between its write and its read.");
    }

    /// <inheritdoc />
    public async ValueTask<CallRecord?> GetAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using var command = _dataSource.CreateCommand(GetSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public ValueTask RenameAsync(string callId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(title);

        return AmendAsync(RenameSql, callId, new NpgsqlParameter { Value = title }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetStatusAsync(string callId, CallStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        return AmendAsync(StatusSql, callId, new NpgsqlParameter { Value = ToText(status) }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetCustomAsync(
        string callId, JsonElement? custom, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        NpgsqlParameter parameter = new()
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = custom is { } element ? element.GetRawText() : DBNull.Value,
        };

        return AmendAsync(CustomSql, callId, parameter, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetExternalIdAsync(
        string callId, string? externalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        NpgsqlParameter parameter = new()
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = externalId is null ? DBNull.Value : externalId,
        };

        return AmendAsync(ExternalIdSql, callId, parameter, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using var command = _dataSource.CreateCommand(DeleteSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<CallPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        CallStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principalKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var hasCursor = CallCursor.TryDecode(after, out var sortAt, out var cursorId);

        await using var command = _dataSource.CreateCommand(ListSql);
        command.Parameters.Add(new NpgsqlParameter { Value = principalKey });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = status is { } narrowed ? ToText(narrowed) : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = hasCursor ? sortAt : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = hasCursor ? cursorId : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });

        List<CallRecord> rows = [];
        DateTimeOffset lastSortAt = default;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadListing(reader));
                lastSortAt = reader.GetFieldValue<DateTimeOffset>(6);
            }
        }

        var next = rows.Count == limit
            ? CallCursor.Encode(lastSortAt, rows[^1].CallId)
            : null;

        return new CallPage(rows, next);
    }

    /// <inheritdoc />
    public async ValueTask<int> SweepAsync(
        TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var swept = 0;

        while (true)
        {
            await using var command = _dataSource.CreateCommand(SweepSql);
            command.Parameters.Add(
                new NpgsqlParameter { Value = retention, NpgsqlDbType = NpgsqlDbType.Interval });
            command.Parameters.Add(new NpgsqlParameter { Value = batchSize });

            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (deleted == 0)
            {
                return swept;
            }

            swept += deleted;
        }
    }

    /// <inheritdoc />
    public async ValueTask AttachPrincipalAsync(
        string callId, string principalKey, string role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(principalKey);
        ArgumentNullException.ThrowIfNull(role);

        await using var command = _dataSource.CreateCommand(AttachSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = principalKey });
        command.Parameters.Add(new NpgsqlParameter { Value = role });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DetachPrincipalAsync(
        string callId, string principalKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(principalKey);

        await using var command = _dataSource.CreateCommand(DetachSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = principalKey });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disposes the pool this store was given.</summary>
    /// <returns>A task that completes when the pool is closed.</returns>
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private async ValueTask AmendAsync(
        string sql, string callId, NpgsqlParameter value, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CallMessage>> AppendAsync(
        string callId,
        IReadOnlyList<CallMessageDraft> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlBatch batch = new(connection);

        NpgsqlBatchCommand appendCommand = new(AppendSql);
        appendCommand.Parameters.Add(new NpgsqlParameter { Value = callId });
        appendCommand.Parameters.Add(new NpgsqlParameter<int?[]>
        {
            TypedValue = [.. messages.Select(message => message.TurnIndex)],
        });
        appendCommand.Parameters.Add(new NpgsqlParameter<string[]>
        {
            TypedValue = [.. messages.Select(message => message.Content.Role.Value)],
        });
        appendCommand.Parameters.Add(new NpgsqlParameter
        {
            Value = messages.Select(message => Serialise(message.Content)).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb,
        });
        appendCommand.Parameters.Add(new NpgsqlParameter<string[]>
        {
            TypedValue = [.. messages.Select(message => message.MessageId)],
        });
        batch.BatchCommands.Add(appendCommand);

        if (state is not null)
        {
            NpgsqlBatchCommand stateCommand = new(StateSql);
            stateCommand.Parameters.Add(new NpgsqlParameter { Value = callId });
            stateCommand.Parameters.Add(new NpgsqlParameter
            {
                Value = JsonSerializer.Serialize(state, CallStateJson.Options),
                NpgsqlDbType = NpgsqlDbType.Jsonb,
            });

            batch.BatchCommands.Add(stateCommand);
        }

        // AppendSql is the only command with a RETURNING clause, so its result set is the only one
        // this reads. Matched by message_id and not by row order: PostgreSQL makes no promise that a
        // multi-row INSERT ... SELECT returns in the order its source rows arrived.
        Dictionary<string, (int Ordinal, int TurnIndex)> written = [];

        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                written[reader.GetString(2)] = (reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        if (written.Count == 0)
        {
            throw new InvalidOperationException($"Store 0 holds no call '{callId}' to append words to.");
        }

        return [.. messages.Select(draft =>
        {
            var (ordinal, turnIndex) = written[draft.MessageId];
            return new CallMessage(callId, ordinal, turnIndex, draft.Content, draft.MessageId);
        })];
    }

    /// <inheritdoc />
    public async ValueTask RewriteAsync(
        string callId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(content);

        await using var command = _dataSource.CreateCommand(RewriteSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = messageId });
        command.Parameters.Add(new NpgsqlParameter { Value = Serialise(content), NpgsqlDbType = NpgsqlDbType.Jsonb });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    public async ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
        string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using var command = _dataSource.CreateCommand(ReadSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });

        List<CallMessage> rows = [];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new CallMessage(
                callId,
                reader.GetInt32(0),
                reader.GetInt32(1),
                Deserialise(reader.GetString(2)),
                reader.GetString(3)));
        }

        return rows;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    public async ValueTask<int> TruncateAsync(
        string callId, int fromOrdinal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using var command = _dataSource.CreateCommand(TruncateSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = fromOrdinal });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    public async ValueTask<int> EraseAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using var command = _dataSource.CreateCommand(EraseSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one call's spoken turns beside the hashes the audit chain holds for them.</summary>
    /// <param name="callId">The call to check.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One row for each spoken turn, oldest turn first.</returns>
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    public Task<IReadOnlyList<TranscriptTurnDigest>> ReadSpokenTurnsAsync(
        string callId, CancellationToken cancellationToken = default)
        => PostgresCallVerification.ReadSpokenTurnsAsync(_dataSource, callId, cancellationToken);
}

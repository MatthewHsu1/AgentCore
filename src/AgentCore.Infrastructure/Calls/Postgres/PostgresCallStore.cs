using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Npgsql;
using NpgsqlTypes;
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

    /// <summary>
    /// One call's row from <see cref="GetSql"/>, whose ninth column is the state a resume reads back.
    /// </summary>
    private static CallRecord Read(NpgsqlDataReader reader) =>
        ReadListing(reader) with { State = reader.IsDBNull(8) ? null : ReadState(reader.GetString(8)) };

    /// <summary>Reads one call's resume blob, or nothing when the blob cannot be read.</summary>
    /// <param name="blob">The JSON in <c>call.state</c>.</param>
    /// <returns>The state, or <see langword="null"/> when it did not parse into one.</returns>
    private static CallSessionState? ReadState(string blob)
    {
        try
        {
            return JsonSerializer.Deserialize<CallSessionState>(blob, CallStateJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One call's row from <see cref="ListSql"/>, which projects <see cref="Projection"/> alone. It
    /// has no ninth column to read, so <see cref="CallRecord.State"/> is left at its default
    /// <see langword="null"/> rather than paying to deserialize a blob a listing never shows.
    /// </summary>
    private static CallRecord ReadListing(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            ToStatus(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : JsonDocument.Parse(reader.GetString(4)).RootElement.Clone(),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

    private static string ToText(CallStatus status) => status == CallStatus.Archived ? Archived : Regular;

    private static CallStatus ToStatus(string text) =>
        text == Archived ? CallStatus.Archived : CallStatus.Regular;

    private async ValueTask AmendAsync(
        string sql, string callId, NpgsqlParameter value, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlBatch batch = new(connection);

        foreach (CallMessage message in messages)
        {
            NpgsqlBatchCommand command = new(AppendSql);
            command.Parameters.Add(new NpgsqlParameter { Value = message.CallId });
            command.Parameters.Add(new NpgsqlParameter { Value = message.Ordinal });
            command.Parameters.Add(new NpgsqlParameter { Value = message.TurnIndex });
            command.Parameters.Add(new NpgsqlParameter { Value = message.Content.Role.Value });
            command.Parameters.Add(
                new NpgsqlParameter { Value = Serialise(message.Content), NpgsqlDbType = NpgsqlDbType.Jsonb });
            command.Parameters.Add(new NpgsqlParameter { Value = message.MessageId });

            batch.BatchCommands.Add(command);
        }

        if (state is not null)
        {
            NpgsqlBatchCommand stateCommand = new(StateSql);
            stateCommand.Parameters.Add(new NpgsqlParameter { Value = messages[0].CallId });
            stateCommand.Parameters.Add(new NpgsqlParameter
            {
                Value = JsonSerializer.Serialize(state, CallStateJson.Options),
                NpgsqlDbType = NpgsqlDbType.Jsonb,
            });

            batch.BatchCommands.Add(stateCommand);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(content);

        await using var command = _dataSource.CreateCommand(RewriteSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = ordinal });
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
    public async Task<IReadOnlyList<TranscriptTurnDigest>> ReadSpokenTurnsAsync(
        string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using var command = _dataSource.CreateCommand(VerifySql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });

        List<TranscriptTurnDigest> turns = [];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            turns.Add(new TranscriptTurnDigest(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return turns;
    }


    private static string Serialise(ChatMessage message)
        => JsonSerializer.Serialize(message, TranscriptJson.Options);

    private static ChatMessage Deserialise(string content)
        => JsonSerializer.Deserialize<ChatMessage>(content, TranscriptJson.Options)
            ?? throw new InvalidOperationException("A call_message row holds JSON null in content.");

}

/// <summary>One spoken turn, as store 1 holds it and as store 3 proves it.</summary>
/// <param name="TurnIndex">The turn, which is the join between the two stores.</param>
/// <param name="Spoken">The words store 1 holds. A barge-in cut them down to what the caller heard.</param>
/// <param name="ReplyTextSha256">
/// The digest the chain holds for the turn, or <see langword="null"/> when its event carried none.
/// </param>
internal sealed record TranscriptTurnDigest(int TurnIndex, string Spoken, string? ReplyTextSha256);

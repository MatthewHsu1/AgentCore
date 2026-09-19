using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Npgsql;
using NpgsqlTypes;
using static AgentCore.Infrastructure.Conversation.Postgres.PostgresConversationRows;
using static AgentCore.Infrastructure.Conversation.Postgres.PostgresConversationStoreSql;

namespace AgentCore.Infrastructure.Conversation.Postgres;

/// <summary>The store, in PostgreSQL: a conversation, who may see it, and its words.</summary>
internal sealed class PostgresConversationStore : IConversationStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over a data source it then owns.</summary>
    /// <param name="dataSource">The pool every statement runs on. Disposing the store disposes it.</param>
    /// <exception cref="ArgumentNullException">The data source is <see langword="null"/>.</exception>
    public PostgresConversationStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using (var command = _dataSource.CreateCommand(CreateSql))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Store 0 lost conversation '{conversationId}' between its write and its read.");
    }

    /// <inheritdoc />
    public async ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(GetSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public ValueTask RenameAsync(string conversationId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(title);

        return AmendAsync(RenameSql, conversationId, new NpgsqlParameter { Value = title }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetStatusAsync(string conversationId, ConversationStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        return AmendAsync(StatusSql, conversationId, new NpgsqlParameter { Value = ToText(status) }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetCustomAsync(
        string conversationId, JsonElement? custom, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        NpgsqlParameter parameter = new()
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = custom is { } element ? element.GetRawText() : DBNull.Value,
        };

        return AmendAsync(CustomSql, conversationId, parameter, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetExternalIdAsync(
        string conversationId, string? externalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        NpgsqlParameter parameter = new()
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = externalId is null ? DBNull.Value : externalId,
        };

        return AmendAsync(ExternalIdSql, conversationId, parameter, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(DeleteSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var continuation = _dataSource.CreateCommand(DeleteContinuationSql);
        continuation.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        await continuation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ConversationPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        ConversationStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principalKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var hasCursor = ConversationCursor.TryDecode(after, out var sortAt, out var cursorId);

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

        List<ConversationRecord> rows = [];
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
            ? ConversationCursor.Encode(lastSortAt, rows[^1].ConversationId)
            : null;

        return new ConversationPage(rows, next);
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
            await using var continuations = _dataSource.CreateCommand(SweepContinuationsSql);

            continuations.Parameters.Add(
                new NpgsqlParameter { Value = retention, NpgsqlDbType = NpgsqlDbType.Interval });

            continuations.Parameters.Add(new NpgsqlParameter { Value = batchSize });
            await continuations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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
        string conversationId, string principalKey, string role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(principalKey);
        ArgumentNullException.ThrowIfNull(role);

        await using var command = _dataSource.CreateCommand(AttachSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        command.Parameters.Add(new NpgsqlParameter { Value = principalKey });
        command.Parameters.Add(new NpgsqlParameter { Value = role });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DetachPrincipalAsync(
        string conversationId, string principalKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(principalKey);

        await using var command = _dataSource.CreateCommand(DetachSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        command.Parameters.Add(new NpgsqlParameter { Value = principalKey });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disposes the pool this store was given.</summary>
    /// <returns>A task that completes when the pool is closed.</returns>
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private async ValueTask AmendAsync(
        string sql, string conversationId, NpgsqlParameter value, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        command.Parameters.Add(value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    [SuppressMessage(
        "csharpsquid",
        "S3265",
        Justification = "Npgsql documents Array combined with an element type via bit OR, and the enum omits Flags only upstream.")]
    public async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
        string conversationId,
        IReadOnlyList<ConversationMessageDraft> messages,
        ConversationSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlBatch batch = new(connection);

        NpgsqlBatchCommand appendCommand = new(AppendSql);
        appendCommand.Parameters.Add(new NpgsqlParameter { Value = conversationId });
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
            stateCommand.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            stateCommand.Parameters.Add(new NpgsqlParameter
            {
                Value = JsonSerializer.Serialize(state, ConversationStateJson.Options),
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
            throw new InvalidOperationException($"Store 0 holds no conversation '{conversationId}' to append words to.");
        }

        return [.. messages.Select(draft =>
        {
            var (ordinal, turnIndex) = written[draft.MessageId];
            return new ConversationMessage(conversationId, ordinal, turnIndex, draft.Content, draft.MessageId);
        })];
    }

    /// <inheritdoc />
    public async ValueTask RewriteAsync(
        string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(content);

        await using var command = _dataSource.CreateCommand(RewriteSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        command.Parameters.Add(new NpgsqlParameter { Value = messageId });
        command.Parameters.Add(new NpgsqlParameter { Value = Serialise(content), NpgsqlDbType = NpgsqlDbType.Jsonb });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
    public async ValueTask<IReadOnlyList<ConversationMessage>> ReadAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(ReadSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

        List<ConversationMessage> rows = [];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ConversationMessage(
                conversationId,
                reader.GetInt32(0),
                reader.GetInt32(1),
                Deserialise(reader.GetString(2)),
                reader.GetString(3)));
        }

        return rows;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
    public async ValueTask<int> TruncateAsync(
        string conversationId, int fromOrdinal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(TruncateSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        command.Parameters.Add(new NpgsqlParameter { Value = fromOrdinal });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The continuation id is <see langword="null"/>.</exception>
    public async ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(EraseSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SaveContinuationAsync(string continuationId, JsonElement envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuationId);

        await using var command = _dataSource.CreateCommand(SaveContinuationSql);
        command.Parameters.Add(new NpgsqlParameter { Value = continuationId });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = envelope.GetRawText(),
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<JsonElement?> GetContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuationId);

        await using var command = _dataSource.CreateCommand(GetContinuationSql);
        command.Parameters.Add(new NpgsqlParameter { Value = continuationId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
    }

    /// <inheritdoc />
    public async ValueTask DeleteContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuationId);

        await using var command = _dataSource.CreateCommand(DeleteContinuationSql);
        command.Parameters.Add(new NpgsqlParameter { Value = continuationId });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }


    /// <summary>Reads one conversation's spoken turns beside the hashes the audit chain holds for them.</summary>
    /// <param name="conversationId">The conversation to check.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One row for each spoken turn, oldest turn first.</returns>
    /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
    public Task<IReadOnlyList<TranscriptTurnDigest>> ReadSpokenTurnsAsync(
        string conversationId, CancellationToken cancellationToken = default)
        => PostgresConversationVerification.ReadSpokenTurnsAsync(_dataSource, conversationId, cancellationToken);
}

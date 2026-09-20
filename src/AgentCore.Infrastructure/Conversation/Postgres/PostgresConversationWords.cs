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

/// <summary>The words of every conversation, in PostgreSQL: store 1's rows, and what a turn writes beside them.</summary>
internal sealed class PostgresConversationWords
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Binds the words to the pool every statement runs on. The store owns the pool; this does not.</summary>
    /// <param name="dataSource">The pool.</param>
    public PostgresConversationWords(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <inheritdoc cref="IConversationStore.AppendAsync"/>
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
        appendCommand.Parameters.Add(new NpgsqlParameter<int?[]>
        {
            TypedValue = [.. messages.Select(message => message.CoversUpTo)],
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
            return new ConversationMessage(conversationId, ordinal, turnIndex, draft.Content, draft.MessageId) { CoversUpTo = draft.CoversUpTo };
        })];
    }

    /// <inheritdoc cref="IConversationStore.RewriteAsync"/>
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

    private static async ValueTask<IReadOnlyList<ConversationMessage>> ReadRowsAsync(
        string conversationId, NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<ConversationMessage> rows = [];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ConversationMessage(
                conversationId,
                reader.GetInt32(0),
                reader.GetInt32(1),
                Deserialise(reader.GetString(2)),
                reader.GetString(3))
            {
                CoversUpTo = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            });
        }

        return rows;
    }

    /// <inheritdoc cref="IConversationStore.ReadForSessionAsync"/>
    /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
    public async ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(ReadForSessionSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

        return await ReadRowsAsync(conversationId, command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IConversationStore.ReadWindowAsync"/>
    /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
    public async ValueTask<IReadOnlyList<ConversationMessage>> ReadWindowAsync(
        string conversationId, TranscriptWindow window, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(ReadWindowSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        command.Parameters.Add(new NpgsqlParameter<int?> { TypedValue = window.BeforeTurn, NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { Value = window.Turns });

        return await ReadRowsAsync(conversationId, command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IConversationStore.OrdinalOfAsync"/>
    /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The message id is <see langword="null"/> or empty.</exception>
    public async ValueTask<int?> OrdinalOfAsync(
        string conversationId, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        await using var command = _dataSource.CreateCommand(OrdinalOfSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        command.Parameters.Add(new NpgsqlParameter { Value = messageId });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int ordinal ? ordinal : null;
    }

    /// <inheritdoc cref="IConversationStore.TruncateAsync"/>
    /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
    public async ValueTask<ConversationCut> TruncateAsync(
        string conversationId, int fromOrdinal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(TruncateSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        command.Parameters.Add(new NpgsqlParameter { Value = fromOrdinal });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        WithdrawnTurns? turns = reader.IsDBNull(1) ? null : new(reader.GetInt32(1), reader.GetInt32(2));
        return new(reader.GetInt32(0), turns);
    }

    /// <inheritdoc cref="IConversationStore.EraseAsync"/>
    /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
    public async ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var command = _dataSource.CreateCommand(EraseSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

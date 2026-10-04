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

namespace AgentCore.Infrastructure.Conversation.Postgres
{
    /// <summary>The words of every conversation, in PostgreSQL: the message store's rows, and what a turn writes beside them.</summary>
    /// <remarks>Binds the words to the pool every statement runs on. The store owns the pool; this does not.</remarks>
    /// <param name="dataSource">The pool.</param>
    internal sealed class PostgresConversationWords(NpgsqlDataSource dataSource)
    {
        private readonly NpgsqlDataSource _dataSource = dataSource;

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

            int? lowestTurn = messages.Min(message => message.TurnIndex);

            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlBatch batch = new(connection);

            NpgsqlBatchCommand appendCommand = new(AppendSql);
            _ = appendCommand.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = appendCommand.Parameters.Add(new NpgsqlParameter<int?[]>
            {
                TypedValue = [.. messages.Select(message => message.TurnIndex)],
            });
            _ = appendCommand.Parameters.Add(new NpgsqlParameter<string[]>
            {
                TypedValue = [.. messages.Select(message => message.Content.Role.Value)],
            });
            _ = appendCommand.Parameters.Add(new NpgsqlParameter
            {
                Value = messages.Select(message => Serialise(message.Content)).ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb,
            });
            _ = appendCommand.Parameters.Add(new NpgsqlParameter<string[]>
            {
                TypedValue = [.. messages.Select(message => message.MessageId)],
            });
            _ = appendCommand.Parameters.Add(new NpgsqlParameter<int?[]>
            {
                TypedValue = [.. messages.Select(message => message.CoversUpTo)],
            });
            _ = appendCommand.Parameters.Add(Turn(lowestTurn));
            batch.BatchCommands.Add(appendCommand);

            if (state is not null)
            {
                NpgsqlBatchCommand stateCommand = new(StateSql);
                _ = stateCommand.Parameters.Add(new NpgsqlParameter { Value = conversationId });
                _ = stateCommand.Parameters.Add(new NpgsqlParameter
                {
                    Value = JsonSerializer.Serialize(state, ConversationStateJson.Options),
                    NpgsqlDbType = NpgsqlDbType.Jsonb,
                });
                _ = stateCommand.Parameters.Add(Turn(lowestTurn));

                batch.BatchCommands.Add(stateCommand);
            }

            // AppendSql is the only command with a RETURNING clause, so its result set is the only one
            // this reads. Matched by message_id and not by row order: PostgreSQL makes no promise that a
            // multi-row INSERT ... SELECT returns in the order its source rows arrived.
            Dictionary<string, (int Ordinal, int TurnIndex)> written = [];

            await using (NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    written[reader.GetString(2)] = (reader.GetInt32(0), reader.GetInt32(1));
                }
            }

            if (written.Count == 0)
            {
                throw await RefusalAsync(connection, conversationId, lowestTurn, cancellationToken).ConfigureAwait(false);
            }

            return [.. messages.Select(draft =>
            {
                (int ordinal, int turnIndex) = written[draft.MessageId];
                return new ConversationMessage(conversationId, ordinal, turnIndex, draft.Content, draft.MessageId) { CoversUpTo = draft.CoversUpTo };
            })];
        }

        private static NpgsqlParameter<int?> Turn(int? lowestTurn)
        {
            return new NpgsqlParameter<int?> { TypedValue = lowestTurn, NpgsqlDbType = NpgsqlDbType.Integer };
        }

        /// <summary>
        /// Names why an append wrote nothing. The read runs after the append's own transaction, so it only picks
        /// the message: the refusal itself was decided atomically by the append.
        /// </summary>
        private static async ValueTask<InvalidOperationException> RefusalAsync(
            NpgsqlConnection connection, string conversationId, int? lowestTurn, CancellationToken cancellationToken)
        {
            if (lowestTurn is { } named)
            {
                await using NpgsqlCommand command = new(NextTurnIndexSql, connection);
                _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int next)
                {
                    return new ConversationTurnConflictException(
                        $"Conversation '{conversationId}' already saved turn {named} and takes turn {next} next, so nothing was written.");
                }
            }

            return new InvalidOperationException($"The conversation store holds no conversation '{conversationId}' to append words to.");
        }

        /// <inheritdoc cref="IConversationStore.RewriteAsync"/>
        public async ValueTask RewriteAsync(
            string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentException.ThrowIfNullOrEmpty(messageId);
            ArgumentNullException.ThrowIfNull(content);

            await using NpgsqlCommand command = _dataSource.CreateCommand(RewriteSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = messageId });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = Serialise(content), NpgsqlDbType = NpgsqlDbType.Jsonb });

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc cref="IConversationStore.DeleteMessageAsync"/>
        public async ValueTask DeleteMessageAsync(
            string conversationId, string messageId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentException.ThrowIfNullOrEmpty(messageId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(DeleteMessageSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = messageId });

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async ValueTask<IReadOnlyList<ConversationMessage>> ReadRowsAsync(
            string conversationId, NpgsqlCommand command, CancellationToken cancellationToken)
        {
            List<ConversationMessage> rows = [];

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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

            await using NpgsqlCommand command = _dataSource.CreateCommand(ReadForSessionSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

            return await ReadRowsAsync(conversationId, command, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc cref="IConversationStore.ReadWindowAsync"/>
        /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
        public async ValueTask<IReadOnlyList<ConversationMessage>> ReadWindowAsync(
            string conversationId, TranscriptWindow window, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(ReadWindowSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = command.Parameters.Add(new NpgsqlParameter<int?> { TypedValue = window.BeforeTurn, NpgsqlDbType = NpgsqlDbType.Integer });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = window.Turns });

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

            await using NpgsqlCommand command = _dataSource.CreateCommand(OrdinalOfSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = messageId });

            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int ordinal ? ordinal : null;
        }

        /// <inheritdoc cref="IConversationStore.TruncateAsync"/>
        /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
        public async ValueTask<ConversationCut> TruncateAsync(
            string conversationId, int fromOrdinal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(TruncateSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = fromOrdinal });

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            WithdrawnTurns? turns = reader.IsDBNull(1) ? null : new(reader.GetInt32(1), reader.GetInt32(2));
            return new(reader.GetInt32(0), turns);
        }

        /// <inheritdoc cref="IConversationStore.EraseAsync"/>
        /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
        public async ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(EraseSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

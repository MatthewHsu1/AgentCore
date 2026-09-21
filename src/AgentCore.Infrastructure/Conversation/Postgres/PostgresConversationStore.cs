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
    /// <summary>The store, in PostgreSQL: a conversation, who may see it, and its words.</summary>
    internal sealed class PostgresConversationStore : IConversationStore, IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;

        private readonly PostgresConversationWords _words;

        /// <summary>Creates the store over a data source it then owns.</summary>
        /// <param name="dataSource">The pool every statement runs on. Disposing the store disposes it.</param>
        /// <exception cref="ArgumentNullException">The data source is <see langword="null"/>.</exception>
        public PostgresConversationStore(NpgsqlDataSource dataSource)
        {
            ArgumentNullException.ThrowIfNull(dataSource);
            _dataSource = dataSource;
            _words = new PostgresConversationWords(dataSource);
        }

        /// <inheritdoc />
        public async ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            await using (NpgsqlCommand command = _dataSource.CreateCommand(CreateSql))
            {
                _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return await GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Store 0 lost conversation '{conversationId}' between its write and its read.");
        }

        /// <inheritdoc />
        public async ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(GetSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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

            await using NpgsqlCommand command = _dataSource.CreateCommand(DeleteSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using NpgsqlCommand continuation = _dataSource.CreateCommand(DeleteContinuationSql);
            _ = continuation.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = await continuation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

            bool hasCursor = ConversationCursor.TryDecode(after, out DateTimeOffset sortAt, out string? cursorId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(ListSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = principalKey });
            _ = command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Text,
                Value = status is { } narrowed ? ToText(narrowed) : DBNull.Value,
            });
            _ = command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.TimestampTz,
                Value = hasCursor ? sortAt : DBNull.Value,
            });
            _ = command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Text,
                Value = hasCursor ? cursorId : DBNull.Value,
            });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = limit });

            List<ConversationRecord> rows = [];
            DateTimeOffset lastSortAt = default;

            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(ReadListing(reader));
                    lastSortAt = reader.GetFieldValue<DateTimeOffset>(6);
                }
            }

            string? next = rows.Count == limit
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

            int swept = 0;

            while (true)
            {
                await using NpgsqlCommand continuations = _dataSource.CreateCommand(SweepContinuationsSql);

                _ = continuations.Parameters.Add(
                    new NpgsqlParameter { Value = retention, NpgsqlDbType = NpgsqlDbType.Interval });

                _ = continuations.Parameters.Add(new NpgsqlParameter { Value = batchSize });
                _ = await continuations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using NpgsqlCommand command = _dataSource.CreateCommand(SweepSql);

                _ = command.Parameters.Add(
                    new NpgsqlParameter { Value = retention, NpgsqlDbType = NpgsqlDbType.Interval });

                _ = command.Parameters.Add(new NpgsqlParameter { Value = batchSize });

                int deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

            await using NpgsqlCommand command = _dataSource.CreateCommand(AttachSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = principalKey });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = role });
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DetachPrincipalAsync(
            string conversationId, string principalKey, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentNullException.ThrowIfNull(principalKey);

            await using NpgsqlCommand command = _dataSource.CreateCommand(DetachSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = command.Parameters.Add(new NpgsqlParameter { Value = principalKey });
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Disposes the pool this store was given.</summary>
        /// <returns>A task that completes when the pool is closed.</returns>
        public ValueTask DisposeAsync()
        {
            return _dataSource.DisposeAsync();
        }

        private async ValueTask AmendAsync(
            string sql, string conversationId, NpgsqlParameter value, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            _ = command.Parameters.Add(value);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
            string conversationId,
            IReadOnlyList<ConversationMessageDraft> messages,
            ConversationSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            return _words.AppendAsync(conversationId, messages, state, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask RewriteAsync(
            string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        {
            return _words.RewriteAsync(conversationId, messageId, content, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
            string conversationId, CancellationToken cancellationToken = default)
        {
            return _words.ReadForSessionAsync(conversationId, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<ConversationMessage>> ReadWindowAsync(
            string conversationId, TranscriptWindow window, CancellationToken cancellationToken = default)
        {
            return _words.ReadWindowAsync(conversationId, window, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<int?> OrdinalOfAsync(
            string conversationId, string messageId, CancellationToken cancellationToken = default)
        {
            return _words.OrdinalOfAsync(conversationId, messageId, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<ConversationCut> TruncateAsync(
            string conversationId, int fromOrdinal, CancellationToken cancellationToken = default)
        {
            return _words.TruncateAsync(conversationId, fromOrdinal, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return _words.EraseAsync(conversationId, cancellationToken);
        }

        /// <inheritdoc />
        public async ValueTask SaveContinuationAsync(string continuationId, JsonElement envelope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(continuationId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(SaveContinuationSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = continuationId });
            _ = command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Jsonb,
                Value = envelope.GetRawText(),
            });
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask<JsonElement?> GetContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(continuationId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(GetContinuationSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = continuationId });

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? null
                : JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
        }

        /// <inheritdoc />
        public async ValueTask DeleteContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(continuationId);

            await using NpgsqlCommand command = _dataSource.CreateCommand(DeleteContinuationSql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = continuationId });
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }


        /// <summary>Reads one conversation's spoken turns beside the hashes the audit chain holds for them.</summary>
        /// <param name="conversationId">The conversation to check.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>One row for each spoken turn, oldest turn first.</returns>
        /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
        public Task<IReadOnlyList<TranscriptTurnDigest>> ReadSpokenTurnsAsync(
            string conversationId, CancellationToken cancellationToken = default)
        {
            return PostgresConversationVerification.ReadSpokenTurnsAsync(_dataSource, conversationId, cancellationToken);
        }
    }
}

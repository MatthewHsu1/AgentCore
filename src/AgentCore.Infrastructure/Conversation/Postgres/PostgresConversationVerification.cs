using Npgsql;
using static AgentCore.Infrastructure.Conversation.Postgres.PostgresConversationStoreSql;

namespace AgentCore.Infrastructure.Conversation.Postgres
{
    /// <summary>Reads the message store's words beside the hashes the audit store holds for them, for a retroactive check.</summary>
    internal static class PostgresConversationVerification
    {
        /// <summary>Reads one conversation's spoken turns beside the hashes the audit chain holds for them.</summary>
        /// <param name="dataSource">The pool the read runs on.</param>
        /// <param name="conversationId">The conversation to check.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>One row for each spoken turn, oldest turn first.</returns>
        /// <exception cref="ArgumentNullException">The conversation id is <see langword="null"/>.</exception>
        public static async Task<IReadOnlyList<TranscriptTurnDigest>> ReadSpokenTurnsAsync(
            NpgsqlDataSource dataSource, string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            await using NpgsqlCommand command = dataSource.CreateCommand(VerifySql);
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

            List<TranscriptTurnDigest> turns = [];

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                turns.Add(new TranscriptTurnDigest(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            return turns;
        }
    }

    /// <summary>One spoken turn, as the message store holds it and as the audit store proves it.</summary>
    /// <param name="TurnIndex">The turn, which is the join between the two stores.</param>
    /// <param name="Spoken">
    /// Every step's text the message store holds for the turn, concatenated in row order. A barge-in cuts these rows
    /// down to what the caller heard, so for a cut turn this already holds the shown words, not the words
    /// the model produced.
    /// </param>
    /// <param name="ReplyTextSha256">
    /// The digest <paramref name="Spoken"/> should hash to: <c>turn.completed</c>'s
    /// <see cref="AgentCore.Domain.Audit.AuditPayloadKeys.ReplyTextSha256"/> for a turn nothing cut, or the
    /// amending <c>reply.interrupted</c>'s
    /// <see cref="AgentCore.Domain.Audit.AuditPayloadKeys.UtteranceUntilInterruptSha256"/> for a turn one did —
    /// the hash of the whole reply the model produced would not match rows that only hold what was shown.
    /// Never <see langword="null"/>: every saved turn has a <c>turn.completed</c> event.
    /// </param>
    internal sealed record TranscriptTurnDigest(int TurnIndex, string Spoken, string? ReplyTextSha256);
}

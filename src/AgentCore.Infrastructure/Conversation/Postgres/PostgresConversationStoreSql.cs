using AgentCore.Infrastructure.Database.Postgres;

namespace AgentCore.Infrastructure.Conversation.Postgres;

/// <summary>Every statement <see cref="PostgresConversationStore"/> runs.</summary>
internal static class PostgresConversationStoreSql
{
    private const string Schema = PostgresSchema.SchemaName;

    internal const string Regular = "regular";

    internal const string Archived = "archived";

    /// <summary>The value a listing sorts and pages by.</summary>
    internal const string SortAt = "COALESCE(m.last_message_at, c.created_at)";

    /// <summary>
    /// The columns a row is read through, and the derived activity time beside them.
    /// </summary>
    internal const string Projection =
        $"""
        c.conversation_id, c.title, c.status, c.external_id, c.custom, c.created_at,
        {SortAt} AS sort_at, m.last_message_at
        """;

    internal const string ActivityJoin =
        $"""
        LEFT JOIN LATERAL (
            SELECT max(updated_at) AS last_message_at FROM {Schema}.conversation_message x WHERE x.conversation_id = c.conversation_id
        ) m ON true
        """;

    internal const string CreateSql =
        $"INSERT INTO {Schema}.conversation (conversation_id) VALUES ($1) ON CONFLICT (conversation_id) DO NOTHING";

    /// <summary>One conversation's row, with the session state a resume reads back and its ordinal counter.</summary>
    internal static readonly string GetSql =
        $"SELECT {Projection}, c.state, c.next_ordinal FROM {Schema}.conversation c {ActivityJoin} WHERE c.conversation_id = $1";

    internal const string StateSql =
        $"UPDATE {Schema}.conversation SET state = $2, updated_at = now() WHERE conversation_id = $1";

    internal const string RenameSql =
        $"UPDATE {Schema}.conversation SET title = $2, updated_at = now() WHERE conversation_id = $1";

    internal const string StatusSql =
        $"UPDATE {Schema}.conversation SET status = $2, updated_at = now() WHERE conversation_id = $1";

    internal const string CustomSql =
        $"UPDATE {Schema}.conversation SET custom = $2, updated_at = now() WHERE conversation_id = $1";

    internal const string ExternalIdSql =
        $"UPDATE {Schema}.conversation SET external_id = $2, updated_at = now() WHERE conversation_id = $1";

    internal const string DeleteSql = $"DELETE FROM {Schema}.conversation WHERE conversation_id = $1";

    internal const string AttachSql =
        $"""
        INSERT INTO {Schema}.conversation_principal (conversation_id, principal_key, role)
        VALUES ($1, $2, $3)
        ON CONFLICT (principal_key, conversation_id) DO NOTHING
        """;

    internal const string DetachSql =
        $"DELETE FROM {Schema}.conversation_principal WHERE conversation_id = $1 AND principal_key = $2";

    /// <summary>One page of one principal's conversations.</summary>
    internal static readonly string ListSql =
        $"""
         SELECT {Projection}
           FROM {Schema}.conversation_principal p
           JOIN {Schema}.conversation c USING (conversation_id)
           {ActivityJoin}
          WHERE p.principal_key = $1
            AND ($2::text IS NULL OR c.status = $2)
            AND ($3::timestamptz IS NULL
                 OR ({SortAt}, c.conversation_id) < ($3, $4))
          ORDER BY sort_at DESC, c.conversation_id DESC
          LIMIT $5
         """;

    /// <summary>
    /// Numbers and inserts every row of one append in a single statement. <c>$5</c> (the message ids)
    /// doubles as the count the whole batch numbers from: <c>cardinality($5::text[])</c> is how many
    /// ordinals <c>mark</c> reserves, and <c>RETURNING</c> on the <c>UPDATE</c> sees the bumped
    /// <c>next_ordinal</c>, so subtracting that same count back off it gives the first one this batch
    /// may use. A null element of <c>$2</c> (turn_index) takes the conversation's own next turn index instead
    /// of naming one, which is what an append from outside any turn asks for.
    /// </summary>
    internal const string AppendSql = $"""
        WITH mark AS (
            UPDATE {Schema}.conversation
               SET next_ordinal = next_ordinal + cardinality($5::text[]), updated_at = now()
             WHERE conversation_id = $1
         RETURNING next_ordinal - cardinality($5::text[]) AS first,
                   coalesce((state ->> 'nextTurnIndex')::int, 0) AS next_turn_index
        )
        INSERT INTO {Schema}.conversation_message (conversation_id, ordinal, turn_index, role, content, message_id)
        SELECT $1, mark.first + d.position - 1, coalesce(d.turn_index, mark.next_turn_index), d.role, d.content, d.message_id
          FROM mark, unnest($2::int[], $3::text[], $4::jsonb[], $5::text[]) WITH ORDINALITY
               AS d(turn_index, role, content, message_id, position)
        RETURNING ordinal, turn_index, message_id
        """;

    /// <summary>Reads one whole conversation.</summary>
    internal const string ReadSql =
        $"""
        SELECT ordinal, turn_index, content, message_id
          FROM {Schema}.conversation_message WHERE conversation_id = $1 ORDER BY ordinal
        """;

    /// <summary>Withdraws the tail of a conversation, from one ordinal onward.</summary>
    internal const string TruncateSql =
        $"DELETE FROM {Schema}.conversation_message WHERE conversation_id = $1 AND ordinal >= $2";

    internal const string RewriteSql = $"""
        UPDATE {Schema}.conversation_message SET content = $3, updated_at = now()
         WHERE conversation_id = $1 AND message_id = $2
        """;

    internal const string EraseSql = $"DELETE FROM {Schema}.conversation_message WHERE conversation_id = $1";

    /// <summary>Files one session envelope under one continuation id, replacing any envelope already there.</summary>
    internal const string SaveContinuationSql =
        $"""
        INSERT INTO {Schema}.response_continuation (store_id, envelope) VALUES ($1, $2)
        ON CONFLICT (store_id) DO UPDATE SET envelope = EXCLUDED.envelope
        """;

    /// <summary>Reads the envelope one continuation id names.</summary>
    internal const string GetContinuationSql =
        $"SELECT envelope FROM {Schema}.response_continuation WHERE store_id = $1";

    /// <summary>Withdraws whatever one continuation id names, if anything.</summary>
    internal const string DeleteContinuationSql =
        $"DELETE FROM {Schema}.response_continuation WHERE store_id = $1";

    /// <summary>
    /// Reads what store 1 holds for each spoken turn of one conversation, beside what store 3 proves.
    /// </summary>
    internal const string VerifySql = $$"""
        WITH spoken AS (
            SELECT DISTINCT ON (conversation_id, turn_index)
                   conversation_id, turn_index,
                   (SELECT coalesce(string_agg(part ->> 'text', '' ORDER BY position), '')
                      FROM jsonb_array_elements(content -> 'contents')
                           WITH ORDINALITY AS element(part, position)
                     WHERE part ->> '$type' = 'text') AS words
              FROM {{Schema}}.conversation_message
             WHERE conversation_id = $1
               AND role = 'assistant'
               AND content -> 'contents' @> '[{"$type": "text"}]'
             ORDER BY conversation_id, turn_index, ordinal DESC
        ),
        completed AS (
            SELECT DISTINCT ON (conversation_id, turn_index) conversation_id, turn_index, payload
              FROM {{Schema}}.audit_event
             WHERE conversation_id = $1 AND kind = 'turn.completed'
             ORDER BY conversation_id, turn_index, sequence DESC
        )
        SELECT m.turn_index, m.words, a.payload ->> 'replyTextSha256'
          FROM spoken m JOIN completed a USING (conversation_id, turn_index)
         ORDER BY m.turn_index
        """;

    /// <summary>Deletes one batch of conversations that have aged out whole.</summary>
    internal static readonly string SweepContinuationsSql =
        $"""
        DELETE FROM {Schema}.response_continuation
        WHERE store_id IN (
          SELECT conversation_id FROM (
            SELECT c.conversation_id
              FROM {Schema}.conversation c
              {ActivityJoin}
             WHERE {SortAt} < now() - $1
             LIMIT $2
          ) q)
        """;

    /// <summary>Deletes one batch of conversations that have aged out whole.</summary>
    internal static readonly string SweepSql =
        $"""
        DELETE FROM {Schema}.conversation
        WHERE conversation_id IN (
          SELECT conversation_id FROM (
            SELECT c.conversation_id
              FROM {Schema}.conversation c
              {ActivityJoin}
             WHERE {SortAt} < now() - $1
             LIMIT $2
          ) q)
        """;
}

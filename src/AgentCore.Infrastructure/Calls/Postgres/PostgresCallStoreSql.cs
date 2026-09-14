using AgentCore.Infrastructure.Database.Postgres;

namespace AgentCore.Infrastructure.Calls.Postgres;

/// <summary>Every statement <see cref="PostgresCallStore"/> runs.</summary>
internal static class PostgresCallStoreSql
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
        c.call_id, c.title, c.status, c.external_id, c.custom, c.created_at,
        {SortAt} AS sort_at, m.last_message_at
        """;

    internal const string ActivityJoin =
        $"""
        LEFT JOIN LATERAL (
            SELECT max(updated_at) AS last_message_at FROM {Schema}.call_message x WHERE x.call_id = c.call_id
        ) m ON true
        """;

    internal const string CreateSql =
        $"INSERT INTO {Schema}.call (call_id) VALUES ($1) ON CONFLICT (call_id) DO NOTHING";

    /// <summary>One call's row, with the session state a resume reads back and its ordinal counter.</summary>
    internal static readonly string GetSql =
        $"SELECT {Projection}, c.state, c.next_ordinal FROM {Schema}.call c {ActivityJoin} WHERE c.call_id = $1";

    internal const string StateSql =
        $"UPDATE {Schema}.call SET state = $2, updated_at = now() WHERE call_id = $1";

    internal const string RenameSql =
        $"UPDATE {Schema}.call SET title = $2, updated_at = now() WHERE call_id = $1";

    internal const string StatusSql =
        $"UPDATE {Schema}.call SET status = $2, updated_at = now() WHERE call_id = $1";

    internal const string CustomSql =
        $"UPDATE {Schema}.call SET custom = $2, updated_at = now() WHERE call_id = $1";

    internal const string ExternalIdSql =
        $"UPDATE {Schema}.call SET external_id = $2, updated_at = now() WHERE call_id = $1";

    internal const string DeleteSql = $"DELETE FROM {Schema}.call WHERE call_id = $1";

    internal const string AttachSql =
        $"""
        INSERT INTO {Schema}.call_principal (call_id, principal_key, role)
        VALUES ($1, $2, $3)
        ON CONFLICT (principal_key, call_id) DO NOTHING
        """;

    internal const string DetachSql =
        $"DELETE FROM {Schema}.call_principal WHERE call_id = $1 AND principal_key = $2";

    /// <summary>One page of one principal's calls.</summary>
    internal static readonly string ListSql =
        $"""
         SELECT {Projection}
           FROM {Schema}.call_principal p
           JOIN {Schema}.call c USING (call_id)
           {ActivityJoin}
          WHERE p.principal_key = $1
            AND ($2::text IS NULL OR c.status = $2)
            AND ($3::timestamptz IS NULL
                 OR ({SortAt}, c.call_id) < ($3, $4))
          ORDER BY sort_at DESC, c.call_id DESC
          LIMIT $5
         """;

    /// <summary>
    /// Numbers and inserts every row of one append in a single statement. <c>$5</c> (the message ids)
    /// doubles as the count the whole batch numbers from: <c>cardinality($5::text[])</c> is how many
    /// ordinals <c>mark</c> reserves, and <c>RETURNING</c> on the <c>UPDATE</c> sees the bumped
    /// <c>next_ordinal</c>, so subtracting that same count back off it gives the first one this batch
    /// may use. A null element of <c>$2</c> (turn_index) takes the call's own next turn index instead
    /// of naming one, which is what an append from outside any turn asks for.
    /// </summary>
    internal const string AppendSql = $"""
        WITH mark AS (
            UPDATE {Schema}.call
               SET next_ordinal = next_ordinal + cardinality($5::text[]), updated_at = now()
             WHERE call_id = $1
         RETURNING next_ordinal - cardinality($5::text[]) AS first,
                   coalesce((state ->> 'nextTurnIndex')::int, 0) AS next_turn_index
        )
        INSERT INTO {Schema}.call_message (call_id, ordinal, turn_index, role, content, message_id)
        SELECT $1, mark.first + d.position - 1, coalesce(d.turn_index, mark.next_turn_index), d.role, d.content, d.message_id
          FROM mark, unnest($2::int[], $3::text[], $4::jsonb[], $5::text[]) WITH ORDINALITY
               AS d(turn_index, role, content, message_id, position)
        RETURNING ordinal, turn_index, message_id
        """;

    /// <summary>Reads one whole call.</summary>
    internal const string ReadSql =
        $"""
        SELECT ordinal, turn_index, content, message_id
          FROM {Schema}.call_message WHERE call_id = $1 ORDER BY ordinal
        """;

    /// <summary>Withdraws the tail of a call, from one ordinal onward.</summary>
    internal const string TruncateSql =
        $"DELETE FROM {Schema}.call_message WHERE call_id = $1 AND ordinal >= $2";

    internal const string RewriteSql = $"""
        UPDATE {Schema}.call_message SET content = $3, updated_at = now()
         WHERE call_id = $1 AND message_id = $2
        """;

    internal const string EraseSql = $"DELETE FROM {Schema}.call_message WHERE call_id = $1";

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
    /// Reads what store 1 holds for each spoken turn of one call, beside what store 3 proves.
    /// </summary>
    internal const string VerifySql = $$"""
        WITH spoken AS (
            SELECT DISTINCT ON (call_id, turn_index)
                   call_id, turn_index,
                   (SELECT coalesce(string_agg(part ->> 'text', '' ORDER BY position), '')
                      FROM jsonb_array_elements(content -> 'contents')
                           WITH ORDINALITY AS element(part, position)
                     WHERE part ->> '$type' = 'text') AS words
              FROM {{Schema}}.call_message
             WHERE call_id = $1
               AND role = 'assistant'
               AND content -> 'contents' @> '[{"$type": "text"}]'
             ORDER BY call_id, turn_index, ordinal DESC
        ),
        completed AS (
            SELECT DISTINCT ON (call_id, turn_index) call_id, turn_index, payload
              FROM {{Schema}}.audit_event
             WHERE call_id = $1 AND kind = 'turn.completed'
             ORDER BY call_id, turn_index, sequence DESC
        )
        SELECT m.turn_index, m.words, a.payload ->> 'replyTextSha256'
          FROM spoken m JOIN completed a USING (call_id, turn_index)
         ORDER BY m.turn_index
        """;

    /// <summary>Deletes one batch of calls that have aged out whole.</summary>
    internal static readonly string SweepContinuationsSql =
        $"""
        DELETE FROM {Schema}.response_continuation
        WHERE store_id IN (
          SELECT call_id FROM (
            SELECT c.call_id
              FROM {Schema}.call c
              {ActivityJoin}
             WHERE {SortAt} < now() - $1
             LIMIT $2
          ) q)
        """;

    /// <summary>Deletes one batch of calls that have aged out whole.</summary>
    internal static readonly string SweepSql =
        $"""
        DELETE FROM {Schema}.call
        WHERE call_id IN (
          SELECT call_id FROM (
            SELECT c.call_id
              FROM {Schema}.call c
              {ActivityJoin}
             WHERE {SortAt} < now() - $1
             LIMIT $2
          ) q)
        """;
}

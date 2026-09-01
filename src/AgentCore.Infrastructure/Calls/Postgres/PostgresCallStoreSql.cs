using AgentCore.Domain.Audit;

namespace AgentCore.Infrastructure.Calls.Postgres;

/// <summary>Every statement <see cref="PostgresCallStore"/> runs.</summary>
internal static class PostgresCallStoreSql
{
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
        """
        LEFT JOIN LATERAL (
            SELECT max(updated_at) AS last_message_at FROM call_message x WHERE x.call_id = c.call_id
        ) m ON true
        """;

    internal const string CreateSql =
        "INSERT INTO call (call_id) VALUES ($1) ON CONFLICT (call_id) DO NOTHING";

    /// <summary>One call's row, with the session state a resume reads back.</summary>
    internal static readonly string GetSql =
        $"SELECT {Projection}, c.state FROM call c {ActivityJoin} WHERE c.call_id = $1";

    internal const string StateSql =
        "UPDATE call SET state = $2, updated_at = now() WHERE call_id = $1";

    internal const string RenameSql =
        "UPDATE call SET title = $2, updated_at = now() WHERE call_id = $1";

    internal const string StatusSql =
        "UPDATE call SET status = $2, updated_at = now() WHERE call_id = $1";

    internal const string CustomSql =
        "UPDATE call SET custom = $2, updated_at = now() WHERE call_id = $1";

    internal const string ExternalIdSql =
        "UPDATE call SET external_id = $2, updated_at = now() WHERE call_id = $1";

    internal const string DeleteSql = "DELETE FROM call WHERE call_id = $1";

    internal const string AttachSql =
        """
        INSERT INTO call_principal (call_id, principal_key, role)
        VALUES ($1, $2, $3)
        ON CONFLICT (principal_key, call_id) DO NOTHING
        """;

    internal const string DetachSql =
        "DELETE FROM call_principal WHERE call_id = $1 AND principal_key = $2";

    /// <summary>One page of one principal's calls.</summary>
    internal static readonly string ListSql =
        $"""
         SELECT {Projection}
           FROM call_principal p
           JOIN call c USING (call_id)
           {ActivityJoin}
          WHERE p.principal_key = $1
            AND ($2::text IS NULL OR c.status = $2)
            AND ($3::timestamptz IS NULL
                 OR ({SortAt}, c.call_id) < ($3, $4))
          ORDER BY sort_at DESC, c.call_id DESC
          LIMIT $5
         """;

    internal const string AppendSql = """
        INSERT INTO call_message (call_id, ordinal, turn_index, role, content)
        VALUES ($1, $2, $3, $4, $5)
        """;

    /// <summary>Reads one whole call.</summary>
    internal const string ReadSql =
        "SELECT ordinal, turn_index, content FROM call_message WHERE call_id = $1 ORDER BY ordinal";

    internal const string RewriteSql = """
        UPDATE call_message SET content = $3, updated_at = now()
         WHERE call_id = $1 AND ordinal = $2
        """;

    internal const string EraseSql = "DELETE FROM call_message WHERE call_id = $1";

    /// <summary>
    /// Reads what store 1 holds for each spoken turn of one call, beside what store 3 proves.
    /// </summary>
    internal const string VerifySql = """
        WITH spoken AS (
            SELECT DISTINCT ON (call_id, turn_index)
                   call_id, turn_index,
                   (SELECT coalesce(string_agg(part ->> 'text', '' ORDER BY position), '')
                      FROM jsonb_array_elements(content -> 'contents')
                           WITH ORDINALITY AS element(part, position)
                     WHERE part ->> '$type' = 'text') AS words
              FROM call_message
             WHERE call_id = $1
               AND role = 'assistant'
               AND content -> 'contents' @> '[{"$type": "text"}]'
             ORDER BY call_id, turn_index, ordinal DESC
        ),
        completed AS (
            SELECT DISTINCT ON (call_id, turn_index) call_id, turn_index, payload
              FROM audit_event
             WHERE call_id = $1 AND kind = 'turn.completed'
             ORDER BY call_id, turn_index, sequence DESC
        )
        SELECT m.turn_index, m.words, a.payload ->> 'replyTextSha256'
          FROM spoken m JOIN completed a USING (call_id, turn_index)
         ORDER BY m.turn_index
        """;

    /// <summary>Deletes one batch of calls that have aged out whole.</summary>
    internal static readonly string SweepSql =
        $"""
        DELETE FROM call
        WHERE call_id IN (
          SELECT call_id FROM (
            SELECT c.call_id
              FROM call c
              {ActivityJoin}
             WHERE {SortAt} < now() - $1
             LIMIT $2
          ) q)
        """;
}

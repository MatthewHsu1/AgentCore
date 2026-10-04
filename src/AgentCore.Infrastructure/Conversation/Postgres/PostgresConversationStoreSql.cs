using AgentCore.Infrastructure.Database.Postgres;

namespace AgentCore.Infrastructure.Conversation.Postgres
{
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

        /// <summary>The turn a conversation takes next, read off its stored state.</summary>
        private const string NextTurnIndex = "coalesce((state ->> 'nextTurnIndex')::int, 0)";

        internal const string CreateSql =
            $"INSERT INTO {Schema}.conversation (conversation_id) VALUES ($1) ON CONFLICT (conversation_id) DO NOTHING";

        /// <summary>One conversation's row, with the session state a resume reads back and its ordinal counter.</summary>
        internal static readonly string GetSql =
            $"SELECT {Projection}, c.state, c.next_ordinal FROM {Schema}.conversation c {ActivityJoin} WHERE c.conversation_id = $1";

        /// <summary>
        /// Writes the state a turn's append carries, under the same turn check as <see cref="AppendSql"/>: it runs in
        /// the append's batch, after it, so a refused append refuses its state too. A state whose next turn is behind
        /// the stored one's is never written, so no append puts back a state older than the one it replaces.
        /// </summary>
        internal static readonly string StateSql =
            $"""
        UPDATE {Schema}.conversation SET state = $2, updated_at = now()
         WHERE conversation_id = $1 AND {TurnUnsaved(3)}
           AND {NextTurnIndex} <= ($2::jsonb ->> 'nextTurnIndex')::int
        """;

        /// <summary>Writes a state no words carry. As with <see cref="StateSql"/>, a state behind the stored one is never written.</summary>
        internal const string SaveStateSql =
            $"""
        UPDATE {Schema}.conversation SET state = $2, updated_at = now()
         WHERE conversation_id = $1 AND {NextTurnIndex} <= ($2::jsonb ->> 'nextTurnIndex')::int
        """;

        /// <summary>Reads the turn a conversation takes next, to tell a refused append from a missing conversation.</summary>
        internal const string NextTurnIndexSql =
            $"SELECT {NextTurnIndex} FROM {Schema}.conversation WHERE conversation_id = $1";

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
        /// of naming one, which is what an append from outside any turn asks for. <c>$6</c> is the
        /// <c>covers_up_to</c> of each row: null for every row somebody said. <c>$7</c> is the lowest turn index
        /// the batch names, or null when it names none; see <see cref="TurnUnsaved"/>.
        /// </summary>
        internal static readonly string AppendSql = $"""
        WITH mark AS (
            UPDATE {Schema}.conversation
               SET next_ordinal = next_ordinal + cardinality($5::text[]), updated_at = now()
             WHERE conversation_id = $1 AND {TurnUnsaved(7)}
         RETURNING next_ordinal - cardinality($5::text[]) AS first,
                   {NextTurnIndex} AS next_turn_index
        )
        INSERT INTO {Schema}.conversation_message (conversation_id, ordinal, turn_index, role, content, message_id, covers_up_to)
        SELECT $1, mark.first + d.position - 1, coalesce(d.turn_index, mark.next_turn_index), d.role, d.content, d.message_id, d.covers_up_to
          FROM mark, unnest($2::int[], $3::text[], $4::jsonb[], $5::text[], $6::int[]) WITH ORDINALITY
               AS d(turn_index, role, content, message_id, covers_up_to, position)
        RETURNING ordinal, turn_index, message_id
        """;

        /// <summary>The columns every row read returns, in the order <c>PostgresConversationWords.ReadRowsAsync</c> reads them.</summary>
        private const string RowColumns = "ordinal, turn_index, content, message_id, covers_up_to";

        /// <summary>
        /// Reads a conversation as its session opens it: the newest summary row, and every row somebody
        /// said above what it covers. With no summary, every row.
        /// </summary>
        internal const string ReadForSessionSql =
            $"""
        WITH summary AS (
            SELECT ordinal, covers_up_to FROM {Schema}.conversation_message
             WHERE conversation_id = $1 AND covers_up_to IS NOT NULL
             ORDER BY ordinal DESC LIMIT 1
        )
        SELECT {RowColumns}
          FROM {Schema}.conversation_message m
         WHERE m.conversation_id = $1
           AND m.ordinal > coalesce((SELECT covers_up_to FROM summary), -1)
           AND (m.covers_up_to IS NULL OR m.ordinal = (SELECT ordinal FROM summary))
         ORDER BY m.ordinal
        """;

        /// <summary>
        /// Reads the newest turns of a conversation before a given one, whole. <c>$2</c> is the turn to
        /// read before, or null for the newest; <c>$3</c> is how many turns at most.
        /// </summary>
        internal const string ReadWindowSql =
            $"""
        SELECT {RowColumns}
          FROM {Schema}.conversation_message
         WHERE conversation_id = $1
           AND covers_up_to IS NULL
           AND turn_index IN (
               SELECT DISTINCT turn_index FROM {Schema}.conversation_message
                WHERE conversation_id = $1 AND covers_up_to IS NULL AND ($2::int IS NULL OR turn_index < $2)
                ORDER BY turn_index DESC LIMIT $3)
         ORDER BY ordinal
        """;

        /// <summary>
        /// Withdraws the tail of a conversation, from one ordinal onward. A summary row goes only when
        /// the cut reaches a row it covers.
        /// </summary>
        internal const string TruncateSql =
            $"""
        WITH gone AS (
            DELETE FROM {Schema}.conversation_message
             WHERE conversation_id = $1 AND ordinal >= $2 AND (covers_up_to IS NULL OR covers_up_to >= $2)
            RETURNING turn_index, covers_up_to)
        SELECT count(*)::int,
               min(turn_index) FILTER (WHERE covers_up_to IS NULL),
               max(turn_index) FILTER (WHERE covers_up_to IS NULL)
          FROM gone
        """;

        /// <summary>Finds the ordinal of one spoken row by its message id. Summary rows are not found.</summary>
        internal const string OrdinalOfSql =
            $"""
        SELECT ordinal FROM {Schema}.conversation_message
         WHERE conversation_id = $1 AND message_id = $2 AND covers_up_to IS NULL
        """;

        internal const string RewriteSql = $"""
        UPDATE {Schema}.conversation_message SET content = $3, updated_at = now()
         WHERE conversation_id = $1 AND message_id = $2
        """;

        internal const string DeleteMessageSql = $"DELETE FROM {Schema}.conversation_message WHERE conversation_id = $1 AND message_id = $2";

        internal const string EraseSql = $"DELETE FROM {Schema}.conversation_message WHERE conversation_id = $1";

        /// <summary>
        /// Records that one response id continues one conversation. A response id is written once, in the
        /// turn's own commit, and never rewritten, so a row already on file is left as it is.
        /// </summary>
        internal const string SaveContinuationSql =
            $"""
        INSERT INTO {Schema}.response_continuation (store_id, conversation_id) VALUES ($1, $2)
        ON CONFLICT (store_id) DO NOTHING
        """;

        /// <summary>Finds the conversation one response id continues.</summary>
        internal const string FindContinuationSql =
            $"SELECT conversation_id FROM {Schema}.response_continuation WHERE store_id = $1";

        /// <summary>Withdraws one response id, if it names a row.</summary>
        internal const string DeleteContinuationSql =
            $"DELETE FROM {Schema}.response_continuation WHERE store_id = $1";

        /// <summary>
        /// Reads what the conversation store holds for each spoken turn of one conversation, beside what the audit chain proves. A line a phone
        /// vendor's front voice spoke (author <c>front_voice</c>, Application's <c>FrontVoice</c>) is not the agent's reply.
        /// </summary>
        internal const string VerifySql = $$"""
        WITH rows AS (
            SELECT conversation_id, turn_index, ordinal,
                   (SELECT coalesce(string_agg(part ->> 'text', '' ORDER BY position), '')
                      FROM jsonb_array_elements(content -> 'contents')
                           WITH ORDINALITY AS element(part, position)
                     WHERE part ->> '$type' = 'text') AS words
              FROM {{Schema}}.conversation_message
             WHERE conversation_id = $1
               AND role = 'assistant'
               AND content ->> 'authorName' IS DISTINCT FROM 'front_voice'
               AND covers_up_to IS NULL
               AND content -> 'contents' @> '[{"$type": "text"}]'
        ),
        spoken AS (
            SELECT conversation_id, turn_index, string_agg(words, '' ORDER BY ordinal) AS words
              FROM rows
             GROUP BY conversation_id, turn_index
        ),
        completed AS (
            SELECT DISTINCT ON (conversation_id, turn_index) conversation_id, turn_index, payload
              FROM {{Schema}}.audit_event
             WHERE conversation_id = $1 AND kind = 'turn.completed'
             ORDER BY conversation_id, turn_index, sequence DESC
        ),
        interrupted AS (
            SELECT DISTINCT ON (conversation_id, turn_index) conversation_id, turn_index, payload
              FROM {{Schema}}.audit_event
             WHERE conversation_id = $1 AND kind = 'reply.interrupted'
             ORDER BY conversation_id, turn_index, sequence DESC
        )
        SELECT m.turn_index, m.words,
               coalesce(i.payload ->> 'utteranceUntilInterruptSha256', a.payload ->> 'replyTextSha256')
          FROM spoken m
          JOIN completed a USING (conversation_id, turn_index)
          LEFT JOIN interrupted i USING (conversation_id, turn_index)
         ORDER BY m.turn_index
        """;

        /// <summary>
        /// Puts or extends a holder's busy mark, unless another holder's mark is still live. The conflict arm's
        /// <c>WHERE</c> leaves a live mark of another holder as it is and returns no row. Lapse times are on the
        /// database's clock, so hosts whose clocks disagree still agree on when a mark lapsed.
        /// </summary>
        internal const string MarkBusySql =
            $"""
        INSERT INTO {Schema}.conversation_busy AS b (conversation_id, holder, busy_until)
        VALUES ($1, $2, now() + $3)
        ON CONFLICT (conversation_id) DO UPDATE
           SET holder = excluded.holder, busy_until = excluded.busy_until
         WHERE b.holder = excluded.holder OR b.busy_until <= now()
        RETURNING true
        """;

        internal const string ClearBusySql =
            $"DELETE FROM {Schema}.conversation_busy WHERE conversation_id = $1 AND holder = $2";

        /// <summary>
        /// Deletes one batch of response ids that have gone untouched past the retention window. A
        /// response id is written once and never rewritten, so this ages a row out from when it was
        /// created. Conversations are never swept: a chat id and every response id it ever advanced age
        /// out on their own, independently of whether the conversation itself is still open.
        /// </summary>
        internal static readonly string SweepContinuationsSql =
            $"""
        DELETE FROM {Schema}.response_continuation
        WHERE store_id IN (
          SELECT store_id FROM {Schema}.response_continuation
           WHERE created_at < now() - $1
           LIMIT $2
        )
        """;

        /// <summary>
        /// Holds when a batch names no turn, or only turns the conversation has not saved. It is a condition on the
        /// conversation row the statement updates, so under READ COMMITTED PostgreSQL re-checks it against the
        /// newest row version after waiting out another writer's lock. A NOT EXISTS over the message rows would
        /// read the snapshot taken before that wait and miss the other writer's turn. The parameter is the batch's
        /// lowest named turn index.
        /// </summary>
        /// <param name="parameter">The position of that parameter in the statement.</param>
        private static string TurnUnsaved(int parameter)
        {
            return $"(${parameter}::int IS NULL OR {NextTurnIndex} <= ${parameter})";
        }
    }
}

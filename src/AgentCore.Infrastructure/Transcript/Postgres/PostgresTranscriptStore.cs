using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Npgsql;
using NpgsqlTypes;

namespace AgentCore.Infrastructure.Transcript.Postgres;

/// <summary>
/// Store 1, the words of a call, in PostgreSQL. One row for each message.
/// </summary>
internal sealed class PostgresTranscriptStore : ITranscriptStore, IAsyncDisposable
{
    private const string AppendSql = """
        INSERT INTO call_message (call_id, ordinal, turn_index, role, content)
        VALUES ($1, $2, $3, $4, $5)
        """;

    /// <summary>Reads one whole call. Step 5, rule 3: this runs at call start and nowhere else.</summary>
    /// <remarks>
    /// The ordinals come back with the rows. A resumed call has to go on allocating where the last
    /// one stopped, and a read that answered with the messages alone would restart at zero and
    /// collide with the rows already written on <c>(call_id, ordinal)</c>.
    /// </remarks>
    private const string ReadSql =
        "SELECT ordinal, turn_index, content FROM call_message WHERE call_id = $1 ORDER BY ordinal";

    private const string RewriteSql = """
        UPDATE call_message SET content = $3, updated_at = now()
         WHERE call_id = $1 AND ordinal = $2
        """;

    private const string EraseSql = "DELETE FROM call_message WHERE call_id = $1";

    /// <summary>
    /// Deletes one batch of calls that have aged out whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A call ages out whole or not at all, so a call whose last message is inside the window keeps
    /// every message it has, however old the first one is.
    /// </para>
    /// <para>
    /// The obvious <c>GROUP BY … HAVING max(updated_at) &lt; …</c> says the same thing and plans as two
    /// sequential scans: 493 ms over 141,545 buffers at 200k rows, against 3.2 ms over 1,029 buffers
    /// here. The <c>LIMIT</c> is what keeps one transaction off a hundred-thousand-row delete, so the
    /// caller loops until a batch deletes nothing.
    /// </para>
    /// </remarks>
    private const string SweepSql = """
        DELETE FROM call_message d
        WHERE d.call_id IN (
          SELECT call_id FROM (
            SELECT m.call_id
              FROM call_message m
             WHERE m.updated_at < now() - $1
             GROUP BY m.call_id
            HAVING NOT EXISTS (SELECT 1 FROM call_message x
                                WHERE x.call_id  = m.call_id
                                  AND x.updated_at >= now() - $1)
             LIMIT $2
          ) q)
        """;

    /// <summary>
    /// Reads what store 1 holds for each spoken turn of one call, beside what store 3 proves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>DISTINCT ON</c> is required.</b> A tool-calling turn writes two assistant messages — one
    /// carrying the tool call, one carrying the spoken reply — so a join on <c>role = 'assistant'</c>
    /// alone returns both. The textless one hashes to the digest of the empty string and reports a
    /// mismatch, which is a false tamper alarm on every tool-calling turn.
    /// </para>
    /// <para>
    /// <b>The spoken words are assembled from the content parts.</b> A stored <c>ChatMessage</c> is
    /// <c>{"role": …, "contents": [{"$type": "text", "text": …}, …]}</c> and carries no <c>text</c>
    /// key of its own — measured, by serialising one. So the text parts are concatenated in their
    /// stored order, which is what <c>ChatMessage.Text</c> does and therefore what the chain hashed.
    /// </para>
    /// <para>
    /// <b>The chain side needs the same guard.</b> A turn may carry more than one
    /// <c>turn.completed</c> — that is what <c>amends_sequence</c> is for — and the join multiplies on
    /// both sides. The latest event of the turn is the one that stands, so it is taken by
    /// <c>sequence DESC</c>.
    /// </para>
    /// <para>
    /// The payload key is <see cref="AuditPayloadKeys.ReplyTextSha256"/>, written out because SQL
    /// takes no constant from C#.
    /// </para>
    /// </remarks>
    private const string VerifySql = """
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

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over a data source it then owns.</summary>
    /// <param name="dataSource">The pool every statement runs on. Disposing the store disposes it.</param>
    /// <exception cref="ArgumentNullException">The data source is <see langword="null"/>.</exception>
    /// <remarks>
    /// The store owns the pool for the reason the audit sink owns its own: nothing above it does, and
    /// a pool left open outlives the host it belonged to.
    /// </remarks>
    public PostgresTranscriptStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One turn is one round trip. The caller allocated the ordinals, so a repeated ordinal is a
    /// unique violation and not a silent overwrite.
    /// </remarks>
    public async ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
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

            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A row that is not there is not written, and this reports nothing about it: the barge-in that
    /// asked for it raced the append it corrects, and the append is what carries the corrected words.
    /// </remarks>
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

    /// <summary>Reads one whole call, oldest message first.</summary>
    /// <param name="callId">The call to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every row of the call, or an empty list when it holds none.</returns>
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    /// <remarks>
    /// <b>A failed read is worse than a failed write, so this one throws.</b> A write that fails costs
    /// the durable record of a turn; a read that answered with an empty history would run the turn
    /// with no memory of the call. It is why the port carries no read at all and why this runs at call
    /// start only — a store unreachable mid-call can then never answer a turn.
    /// </remarks>
    public async Task<IReadOnlyList<CallMessage>> ReadAsync(
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
                Deserialise(reader.GetString(2))));
        }

        return rows;
    }

    /// <summary>Erases one caller: every word of one call, and nothing else.</summary>
    /// <param name="callId">The call to erase.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many rows went.</returns>
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    /// <remarks>
    /// The audit chain keeps its rows and stays intact, because it holds a hash of what was spoken
    /// and never the words. What the erased call proves, it goes on proving.
    /// </remarks>
    public async Task<int> EraseAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using var command = _dataSource.CreateCommand(EraseSql);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes every call whose last message is older than the retention window.</summary>
    /// <param name="retention">
    /// How long a call is kept, measured from its most recently written message. The window belongs to
    /// a deployment: it is not a schema key, and nothing here defaults it.
    /// </param>
    /// <param name="batchSize">
    /// How many calls one transaction may delete. 500 is what the plan above was measured at; the
    /// sweep loops until a batch deletes nothing, so this bounds one transaction and never the work.
    /// </param>
    /// <param name="cancellationToken">Cancels the sweep between batches, and inside one.</param>
    /// <returns>How many rows went, over every batch.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The retention window is negative, or the batch size is not positive.
    /// </exception>
    /// <remarks>
    /// Nightly, and off the call path. Each batch is its own transaction, so a sweep of a large table
    /// never holds one open across the whole delete and a cancelled sweep keeps the batches it
    /// finished.
    /// </remarks>
    public async Task<int> SweepAsync(
        TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var swept = 0;

        while (true)
        {
            await using var command = _dataSource.CreateCommand(SweepSql);
            command.Parameters.Add(new NpgsqlParameter { Value = retention, NpgsqlDbType = NpgsqlDbType.Interval });
            command.Parameters.Add(new NpgsqlParameter { Value = batchSize });

            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (deleted == 0)
            {
                return swept;
            }

            swept += deleted;
        }
    }

    /// <summary>Reads one call's spoken turns beside the hashes the audit chain holds for them.</summary>
    /// <param name="callId">The call to check.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One row for each spoken turn, oldest turn first.</returns>
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    /// <remarks>
    /// This is the check that store 1 still holds the words store 3 proves: hash each
    /// <see cref="TranscriptTurnDigest.Spoken"/> with <c>AuditHash.OfText</c> and compare. A turn
    /// whose words were erased answers with no row at all, which is the erasure working rather than a
    /// tamper.
    /// </remarks>
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

    /// <summary>Disposes the pool this store was given.</summary>
    /// <returns>A task that completes when the pool is closed.</returns>
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    /// <remarks>
    /// <see cref="TranscriptJson.Options"/> is required here, not merely convenient: the column is
    /// <c>jsonb</c>, which keeps no key order, so a stored <c>$type</c> discriminator does not come
    /// back first and every read throws without <c>AllowOutOfOrderMetadataProperties</c> — measured, on
    /// the first run of these tests. The alternative was the <c>json</c> column type, which keeps the
    /// text verbatim and gives up every <c>jsonb</c> operator the verify query above is written in,
    /// <c>@&gt;</c> among them.
    /// </remarks>
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

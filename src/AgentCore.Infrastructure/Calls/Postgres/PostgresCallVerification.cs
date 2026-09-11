using Npgsql;
using static AgentCore.Infrastructure.Calls.Postgres.PostgresCallStoreSql;

namespace AgentCore.Infrastructure.Calls.Postgres;

/// <summary>Reads store 1's words beside the hashes store 3 holds for them, for a retroactive check.</summary>
internal static class PostgresCallVerification
{
    /// <summary>Reads one call's spoken turns beside the hashes the audit chain holds for them.</summary>
    /// <param name="dataSource">The pool the read runs on.</param>
    /// <param name="callId">The call to check.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One row for each spoken turn, oldest turn first.</returns>
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    public static async Task<IReadOnlyList<TranscriptTurnDigest>> ReadSpokenTurnsAsync(
        NpgsqlDataSource dataSource, string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        await using var command = dataSource.CreateCommand(VerifySql);
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
}

/// <summary>One spoken turn, as store 1 holds it and as store 3 proves it.</summary>
/// <param name="TurnIndex">The turn, which is the join between the two stores.</param>
/// <param name="Spoken">The words store 1 holds. A barge-in cut them down to what the caller heard.</param>
/// <param name="ReplyTextSha256">
/// The digest the chain holds for the turn, or <see langword="null"/> when its event carried none.
/// </param>
internal sealed record TranscriptTurnDigest(int TurnIndex, string Spoken, string? ReplyTextSha256);

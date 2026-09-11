using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Npgsql;
using static AgentCore.Infrastructure.Calls.Postgres.PostgresCallStoreSql;

namespace AgentCore.Infrastructure.Calls.Postgres;

/// <summary>How a Postgres row becomes a <see cref="CallRecord"/> or <see cref="ChatMessage"/>, and back.</summary>
internal static class PostgresCallRows
{
    /// <summary>
    /// One call's row from <see cref="PostgresCallStoreSql.GetSql"/>, whose ninth column is the state a
    /// resume reads back and whose tenth is its ordinal counter.
    /// </summary>
    internal static CallRecord Read(NpgsqlDataReader reader) =>
        ReadListing(reader) with
        {
            State = reader.IsDBNull(8) ? null : ReadState(reader.GetString(8)),
            NextOrdinal = reader.GetInt32(9),
        };

    /// <summary>Reads one call's resume blob, or nothing when the blob cannot be read.</summary>
    /// <param name="blob">The JSON in <c>call.state</c>.</param>
    /// <returns>The state, or <see langword="null"/> when it did not parse into one.</returns>
    internal static CallSessionState? ReadState(string blob)
    {
        try
        {
            return JsonSerializer.Deserialize<CallSessionState>(blob, CallStateJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One call's row from <see cref="PostgresCallStoreSql.ListSql"/>, which projects
    /// <see cref="PostgresCallStoreSql.Projection"/> alone. It has no ninth or tenth column to read, so
    /// <see cref="CallRecord.State"/> and <see cref="CallRecord.NextOrdinal"/> are left at their
    /// defaults — <see langword="null"/> and 0 — rather than paying for what a listing never shows.
    /// </summary>
    internal static CallRecord ReadListing(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            ToStatus(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : JsonDocument.Parse(reader.GetString(4)).RootElement.Clone(),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

    internal static string ToText(CallStatus status) => status == CallStatus.Archived ? Archived : Regular;

    internal static CallStatus ToStatus(string text) =>
        text == Archived ? CallStatus.Archived : CallStatus.Regular;

    internal static string Serialise(ChatMessage message)
        => JsonSerializer.Serialize(message, TranscriptJson.Options);

    internal static ChatMessage Deserialise(string content)
        => JsonSerializer.Deserialize<ChatMessage>(content, TranscriptJson.Options)
            ?? throw new InvalidOperationException("A call_message row holds JSON null in content.");
}

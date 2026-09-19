using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Npgsql;
using static AgentCore.Infrastructure.Conversation.Postgres.PostgresConversationStoreSql;

namespace AgentCore.Infrastructure.Conversation.Postgres;

/// <summary>How a Postgres row becomes a <see cref="ConversationRecord"/> or <see cref="ChatMessage"/>, and back.</summary>
internal static class PostgresConversationRows
{
    /// <summary>
    /// One conversation's row from <see cref="PostgresConversationStoreSql.GetSql"/>, whose ninth column is the state a
    /// resume reads back and whose tenth is its ordinal counter.
    /// </summary>
    internal static ConversationRecord Read(NpgsqlDataReader reader) =>
        ReadListing(reader) with
        {
            State = reader.IsDBNull(8) ? null : ReadState(reader.GetString(8)),
            NextOrdinal = reader.GetInt32(9),
        };

    /// <summary>Reads one conversation's resume blob, or nothing when the blob cannot be read.</summary>
    /// <param name="blob">The JSON in <c>conversation.state</c>.</param>
    /// <returns>The state, or <see langword="null"/> when it did not parse into one.</returns>
    internal static ConversationSessionState? ReadState(string blob)
    {
        try
        {
            return JsonSerializer.Deserialize<ConversationSessionState>(blob, ConversationStateJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One conversation's row from <see cref="PostgresConversationStoreSql.ListSql"/>, which projects
    /// <see cref="PostgresConversationStoreSql.Projection"/> alone. It has no ninth or tenth column to read, so
    /// <see cref="ConversationRecord.State"/> and <see cref="ConversationRecord.NextOrdinal"/> are left at their
    /// defaults — <see langword="null"/> and 0 — rather than paying for what a listing never shows.
    /// </summary>
    internal static ConversationRecord ReadListing(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            ToStatus(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : JsonDocument.Parse(reader.GetString(4)).RootElement.Clone(),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

    internal static string ToText(ConversationStatus status) => status == ConversationStatus.Archived ? Archived : Regular;

    internal static ConversationStatus ToStatus(string text) =>
        text == Archived ? ConversationStatus.Archived : ConversationStatus.Regular;

    internal static string Serialise(ChatMessage message)
        => JsonSerializer.Serialize(message, TranscriptJson.Options);

    internal static ChatMessage Deserialise(string content)
        => JsonSerializer.Deserialize<ChatMessage>(content, TranscriptJson.Options)
            ?? throw new InvalidOperationException("A conversation_message row holds JSON null in content.");
}

using System.Text.Json;

namespace AgentCore.Application.Conversation
{
    /// <summary>One conversation, apart from its words. It is one row of the conversation store.</summary>
    /// <param name="ConversationId">The conversation this describes. It is the join to stores 1 and 3.</param>
    /// <param name="Title">What to show in a list, or <see langword="null"/> until one is made.</param>
    /// <param name="Status">Whether the conversation is still listed as usual.</param>
    /// <param name="ExternalId">A consumer's own id for the conversation, or <see langword="null"/>.</param>
    /// <param name="Custom">A consumer's own fields, or <see langword="null"/>.</param>
    /// <param name="CreatedAt">When the conversation row was made. UTC.</param>
    /// <param name="LastMessageAt">
    /// When the message store last wrote a word of this conversation, or <see langword="null"/> when it holds none. UTC.
    /// Derived at read time; the conversation store keeps no such column.
    /// </param>
    /// <param name="State">
    /// What the session of this conversation held, or <see langword="null"/> for a conversation that has run no turn
    /// under a build that writes it. Read by <c>GetAsync</c> and <c>CreateAsync</c> only: a listing does
    /// not need it and does not pay for it.
    /// </param>
    /// <param name="NextOrdinal">
    /// The ordinal the conversation's next appended row takes. Read by <c>GetAsync</c> and <c>CreateAsync</c>
    /// only, on the same terms as <paramref name="State"/>: a listing leaves it at its default 0 rather
    /// than paying to read it.
    /// </param>
    public sealed record ConversationRecord(
        string ConversationId,
        string? Title,
        ConversationStatus Status,
        string? ExternalId,
        JsonElement? Custom,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastMessageAt,
        ConversationSessionState? State = null,
        int NextOrdinal = 0);
}

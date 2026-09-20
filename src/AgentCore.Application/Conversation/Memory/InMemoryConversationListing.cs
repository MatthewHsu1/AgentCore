namespace AgentCore.Application.Conversation.Memory;

/// <summary>How the in-memory store ranks and pages one principal's conversations.</summary>
internal static class InMemoryConversationListing
{
    /// <summary>Takes one page of conversations, most recently active first.</summary>
    /// <param name="claimed">Every conversation the principal has a claim on.</param>
    /// <param name="after">A cursor from an earlier page, or <see langword="null"/> for the first.</param>
    /// <param name="limit">How many rows this page may hold.</param>
    /// <param name="status">The one status to return, or <see langword="null"/> for every status.</param>
    /// <returns>The page, and a cursor when a following page exists.</returns>
    public static ConversationPage Page(
        IEnumerable<ConversationRecord> claimed, string? after, int limit, ConversationStatus? status)
    {
        var hasCursor = ConversationCursor.TryDecode(after, out var sortAt, out var cursorId);

        var ordered = claimed
            .Where(conversation => status is null || conversation.Status == status)
            .Where(conversation => !hasCursor
                || SortValue(conversation) < sortAt
                || (SortValue(conversation) == sortAt
                    && string.CompareOrdinal(conversation.ConversationId, cursorId) < 0))
            .OrderByDescending(SortValue)
            .ThenByDescending(conversation => conversation.ConversationId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var next = ordered.Count == limit
            ? ConversationCursor.Encode(SortValue(ordered[^1]), ordered[^1].ConversationId)
            : null;

        return new ConversationPage(ordered, next);
    }

    /// <summary>The clock a conversation is ranked by: its last message, or its making when it holds none.</summary>
    /// <param name="conversation">The conversation to rank.</param>
    public static DateTimeOffset SortValue(ConversationRecord conversation)
        => conversation.LastMessageAt ?? conversation.CreatedAt;
}

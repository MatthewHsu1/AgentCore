namespace AgentCore.Application.Hooks
{
    /// <summary>Where and when one gate or notice happens. Every gate object and every notice carries one.</summary>
    /// <param name="ConversationId">
    /// The conversation, or <see langword="null"/> for a host notice and for the call and entry gates, which run
    /// before a conversation exists.
    /// </param>
    /// <param name="Entry">The entry the conversation runs, or <see langword="null"/> before one is chosen.</param>
    /// <param name="TurnIndex">The zero-based turn, or <see langword="null"/> for a fact about the conversation itself.</param>
    /// <param name="Stage">The stage the machine held, empty when the entry declares no policy, or <see langword="null"/>.</param>
    /// <param name="SessionId">
    /// The loaded session instance. A version 7 Guid, so it sorts by load time. <see cref="Guid.Empty"/> on a host
    /// notice and on a gate that runs before a session exists.
    /// </param>
    /// <param name="Sequence">
    /// The notice's place among the notices of its conversation while the conversation stays loaded, from 1.
    /// Ordering across an unload uses <paramref name="SessionId"/> first. Gates carry 0.
    /// </param>
    /// <param name="OccurredAt">When it happened, on the session's clock.</param>
    public sealed record HookScope(
        string? ConversationId,
        string? Entry,
        int? TurnIndex,
        string? Stage,
        Guid SessionId,
        long Sequence,
        DateTimeOffset OccurredAt);
}

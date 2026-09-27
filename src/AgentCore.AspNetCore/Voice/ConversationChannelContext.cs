namespace AgentCore.AspNetCore.Voice
{
    /// <summary>What one conversation needs to open its channel.</summary>
    /// <param name="ConversationId">The AgentCore conversation id, which the channel must not invent for itself.</param>
    /// <param name="CustomParameters">Per-conversation values the host attached, or <see langword="null"/>.</param>
    public sealed record ConversationChannelContext(string ConversationId, IReadOnlyDictionary<string, string>? CustomParameters);
}

namespace AgentCore.Application.Conversation;

/// <summary>What one conversation needs to open its channel.</summary>
/// <param name="ConversationId">The AgentCore conversation id, which the channel must not invent for itself.</param>
/// <param name="CustomParameters">Per-conversation values the host attached, or <see langword="null"/>.</param>
/// <remarks>
/// This carries no vendor field and no wire frame, so D8 holds: a factory learns what the conversation is
/// without the core learning what the vendor is.
/// </remarks>
public sealed record ConversationChannelContext(string ConversationId, IReadOnlyDictionary<string, string>? CustomParameters);

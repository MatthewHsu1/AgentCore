using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>One message to write to a call. The store gives it its ordinal.</summary>
/// <param name="TurnIndex">
/// The turn the message belongs to, or <see langword="null"/> for a message written outside any
/// turn, which the store stamps with the turn the call's session takes next.
/// </param>
/// <param name="Content">The message itself.</param>
/// <param name="MessageId">What the message is called. Unique within the call. Never empty.</param>
public sealed record CallMessageDraft(int? TurnIndex, ChatMessage Content, string MessageId);

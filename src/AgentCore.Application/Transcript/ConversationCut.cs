namespace AgentCore.Application.Transcript;

/// <summary>What one <see cref="Ports.IConversationStore.TruncateAsync"/> took.</summary>
/// <param name="Rows">How many rows went, a summary row among them.</param>
/// <param name="Turns">The span of turns whose spoken rows went, or <see langword="null"/> when none did.</param>
public readonly record struct ConversationCut(int Rows, WithdrawnTurns? Turns);

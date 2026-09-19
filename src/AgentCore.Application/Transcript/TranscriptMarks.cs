namespace AgentCore.Application.Transcript;

/// <summary>How far a conversation has got, in places rather than in counts.</summary>
/// <param name="NextOrdinal">The next ordinal the conversation issues.</param>
/// <param name="NextTurnIndex">The index the conversation's next turn takes.</param>
internal readonly record struct TranscriptMarks(int NextOrdinal, int NextTurnIndex);

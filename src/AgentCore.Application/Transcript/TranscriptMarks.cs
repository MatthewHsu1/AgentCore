namespace AgentCore.Application.Transcript;

/// <summary>How far a call has got, in places rather than in counts.</summary>
/// <param name="NextOrdinal">The next ordinal the call issues.</param>
/// <param name="NextTurnIndex">The index the call's next turn takes.</param>
internal readonly record struct TranscriptMarks(int NextOrdinal, int NextTurnIndex);

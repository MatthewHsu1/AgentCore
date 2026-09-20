namespace AgentCore.Application.Transcript;

/// <summary>What a compaction of the conversation would cover, read before the compaction runs.</summary>
/// <param name="Revision">The <see cref="ConversationTranscript.Revision"/> the words stood at when read.</param>
/// <param name="CoversUpTo">The last ordinal the summary that already stands covers, or <see langword="null"/> when none stands.</param>
/// <param name="Messages">Every message the compaction covers, as the model sees them, oldest first. A standing summary comes first.</param>
internal readonly record struct TranscriptFloor(int Revision, int? CoversUpTo, IReadOnlyList<ViewMessage> Messages);

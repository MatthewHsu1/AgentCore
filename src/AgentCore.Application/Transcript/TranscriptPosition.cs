namespace AgentCore.Application.Transcript
{
    /// <summary>Where a session's words stood at one moment, read under the conversation's lock.</summary>
    /// <param name="Written">Completes when every store 1 write queued up to that moment has landed.</param>
    /// <param name="Revision">The transcript's revision, which every later change of the words moves.</param>
    /// <param name="NextOrdinal">The next ordinal as the session counted it.</param>
    internal readonly record struct TranscriptPosition(Task Written, int Revision, int NextOrdinal);
}

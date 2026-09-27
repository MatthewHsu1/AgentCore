namespace AgentCore.Application.Transcript
{
    /// <summary>What a session's lost writes came to, judged against one read of the store.</summary>
    /// <param name="Mark">The last loss counted before the read. Later losses are not judged.</param>
    /// <param name="Overtaken">Whether the store held another session's writes in place of the losses.</param>
    internal readonly record struct TranscriptLossVerdict(int Mark, bool Overtaken);
}

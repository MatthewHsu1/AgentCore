using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    /// <summary>What this turn adds to the transcript.</summary>
    /// <param name="Heard">The assistant message the caller heard, or <see langword="null"/> when nothing was.</param>
    /// <param name="Written">Every message store 0 appends after the caller's own; empty for rows 3 and 4.</param>
    internal sealed record TranscriptEntry(ChatMessage? Heard, List<ChatMessage> Written);
}

using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>What one Responses request asks to run, read off the body.</summary>
    /// <param name="ConversationId">The conversation the request named, or <see langword="null"/>.</param>
    /// <param name="Messages">The messages of the run. Empty for an approval-only body.</param>
    /// <param name="ContinuationKey">
    /// The session-store key this turn hangs off, or <see langword="null"/> for the first turn of a
    /// conversation. A response id names one past turn; a conversation id names the conversation itself.
    /// </param>
    internal sealed record ResponsesRunInput(
        string? ConversationId,
        IReadOnlyList<ChatMessage> Messages,
        string? ContinuationKey);
}

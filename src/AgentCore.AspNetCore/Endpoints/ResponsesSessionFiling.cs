using AgentCore.AspNetCore.Sessions;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>Files a response id the Responses path just minted, so it later resolves to its conversation.</summary>
    internal static class ResponsesSessionFiling
    {
        /// <summary>Records that one response id continues one conversation.</summary>
        /// <param name="sessions">The store to file the response id in.</param>
        /// <param name="responseId">The id minted for this turn's answer.</param>
        /// <param name="ownerConversationId">The conversation the response id continues.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        internal static async Task SaveAsync(
            AgentCoreAgentSessionStore sessions,
            string responseId,
            string ownerConversationId,
            CancellationToken cancellationToken)
        {
            await sessions.SaveContinuationAsync(responseId, ownerConversationId, cancellationToken).ConfigureAwait(false);
        }
    }
}

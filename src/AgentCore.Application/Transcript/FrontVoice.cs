using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// The lines a phone vendor's own voice spoke to the caller with no turn of ours behind them, such as GPT-Live
    /// answering a small question itself. They are stored as assistant messages under <see cref="AuthorName"/>, so
    /// the agent reads them as said, and nothing counts them as the agent's reply.
    /// </summary>
    internal static class FrontVoice
    {
        /// <summary>The author of every front-voice line. The Postgres verify query names it too.</summary>
        internal const string AuthorName = "front_voice";

        /// <summary>Builds one front-voice line.</summary>
        internal static ChatMessage Line(string text)
        {
            return new(ChatRole.Assistant, text) { AuthorName = AuthorName };
        }

        /// <summary>Whether a message is the agent's own reply: an assistant message no front voice spoke.</summary>
        internal static bool IsAgentReply(ChatMessage message)
        {
            return message.Role == ChatRole.Assistant && !string.Equals(message.AuthorName, AuthorName, StringComparison.Ordinal);
        }
    }
}

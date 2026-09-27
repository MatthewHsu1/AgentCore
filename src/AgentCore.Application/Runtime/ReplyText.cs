using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// Picks the one message of a run that the caller actually hears.
    /// </summary>
    internal static class ReplyText
    {
        /// <summary>Reads the words this run puts in the caller's ear.</summary>
        /// <param name="messages">The messages of the run, oldest first.</param>
        /// <param name="outputAgents">
        /// The <c>agents.items</c> ids whose reply the caller hears, or <see langword="null"/> when every
        /// message counts. <c>CompiledAgent.OutputAgents</c> names them.
        /// </param>
        /// <returns>The spoken text, or an empty string when the run produced none.</returns>
        internal static string From(IList<ChatMessage> messages, IReadOnlySet<string>? outputAgents = null)
        {
            for (int index = messages.Count - 1; index >= 0; index--)
            {
                ChatMessage message = messages[index];

                if (message.Contents.Count == 0)
                {
                    continue;
                }

                if (outputAgents is not null && (message.AuthorName is not { } author || !outputAgents.Contains(author)))
                {
                    continue;
                }

                return message.Text;
            }

            return string.Empty;
        }
    }
}

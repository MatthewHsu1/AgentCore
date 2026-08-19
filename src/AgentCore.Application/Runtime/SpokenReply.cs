using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Picks the one message of a run that the caller actually hears.
/// </summary>
/// <remarks>
/// <para>
/// <c>AgentResponse.Text</c> concatenates every message of the response. On a graph row that is every
/// node's reply, so the caller would hear the graph deliberating. The last message is the spoken one
/// — but only after two artefacts are stepped over, and both were measured on Microsoft.Agents.AI
/// 1.17.0.
/// </para>
/// <para>
/// <b>A folded stream is mostly lifecycle.</b> Collecting a two-node graph's updates and calling
/// <c>ToAgentResponse()</c> gave 25 messages for two replies: 23 of them carried no content at all,
/// and ten of those sat after the last spoken word. Reading the last message flatly would therefore
/// report every streaming graph turn as an empty reply.
/// </para>
/// <para>
/// <b>A quiet node vanishes when buffered.</b> A sequential graph whose last node answers with
/// whitespace returns one message, and it is the node BEFORE it. Naming the agents the caller hears
/// is what keeps that deliberation out of the caller's ear.
/// </para>
/// </remarks>
internal static class SpokenReply
{
    /// <summary>Reads the words this run puts in the caller's ear.</summary>
    /// <param name="messages">The messages of the run, oldest first.</param>
    /// <param name="spokenBy">
    /// The <c>agents.items</c> ids whose reply the caller hears, or <see langword="null"/> when every
    /// message counts. <c>CompiledAgent.SpokenBy</c> names them.
    /// </param>
    /// <returns>The spoken text, or an empty string when the run produced none.</returns>
    internal static string From(IList<ChatMessage> messages, IReadOnlySet<string>? spokenBy = null)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];

            // A message with no content at all is a workflow lifecycle event, not speech. A message
            // that carries content and no words is a reply that said nothing, which IS the failure.
            if (message.Contents.Count == 0)
            {
                continue;
            }

            if (spokenBy is not null && (message.AuthorName is not { } author || !spokenBy.Contains(author)))
            {
                continue;
            }

            return message.Text;
        }

        return string.Empty;
    }
}

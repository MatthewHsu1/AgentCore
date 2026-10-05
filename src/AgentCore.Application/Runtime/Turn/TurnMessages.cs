using System.Text;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// The message shapes one turn builds and filters. Every member is pure: messages in, messages out.
    /// </summary>
    internal static class TurnMessages
    {
        /// <summary>
        /// What opens the <c>system</c> message a graph row's history rides on.
        /// </summary>
        internal const string HistoryPreamble = "Conversation so far:\n";

        /// <summary>
        /// What names the caller in that message.
        /// </summary>
        internal const string UserLinePrefix = "User: ";

        /// <summary>
        /// What names this agent in that message.
        /// </summary>
        internal const string AgentLinePrefix = "You: ";

        /// <summary>
        /// Renders the conversation so far into the one role a workflow node still recognises.
        /// </summary>
        /// <param name="history">The caller-facing history of this conversation, oldest first.</param>
        /// <returns>One <c>system</c> message, or <see langword="null"/> on the first turn of a conversation.</returns>
        internal static ChatMessage? GraphHistory(IReadOnlyList<ChatMessage> history)
        {
            StringBuilder rendered = new();

            foreach (ChatMessage message in history)
            {
                if (message.Text is not { Length: > 0 } text)
                {
                    continue;
                }

                _ = rendered
                    .Append(message.Role == ChatRole.User ? UserLinePrefix : AgentLinePrefix)
                    .Append(text)
                    .Append('\n');
            }

            return rendered.Length == 0
                ? null
                : new ChatMessage(ChatRole.System, HistoryPreamble + rendered.ToString().TrimEnd('\n'));
        }

        /// <summary>Reads whether one update carries something a host needs.</summary>
        /// <param name="update">One update of the run.</param>
        /// <returns>Whether the host reads it.</returns>
        internal static bool CarriesContent(ChatResponseUpdate update)
        {
            return update.Contents.Any(content => content is not TextContent text || text.Text.Length > 0);
        }

        /// <summary>Keeps the words and the tool calls whose results arrived, and drops every unpaired call.</summary>
        /// <param name="messages">Every message the round produced.</param>
        /// <returns>The words and the complete call-and-result pairs, in their original order.</returns>
        internal static List<ChatMessage> FinishedMessages(IList<ChatMessage> messages)
        {
            HashSet<string> answered = [];
            foreach (ChatMessage message in messages)
            {
                foreach (FunctionResultContent result in message.Contents.OfType<FunctionResultContent>())
                {
                    _ = answered.Add(result.CallId);
                }
            }

            List<ChatMessage> kept = [];
            foreach (ChatMessage message in messages)
            {
                // A parallel round can finish one call and leave a sibling call in the same message
                // mid-flight. The rule is per call id and not per message, so only the unfinished call
                // is stripped out; the finished one, whose side effect already ran, stays in place.
                List<AIContent> tools =
                [
                    .. message.Contents.Where(content => content switch
                    {
                        FunctionCallContent call => answered.Contains(call.CallId),
                        _ => true,
                    }),
                ];

                if (!tools.Any(content => content is FunctionCallContent or FunctionResultContent or ToolApprovalRequestContent
                    || content is TextContent { Text.Length: > 0 }))
                {
                    // A message whose every call is still in flight, with no words beside them. It does not
                    // belong in the next turn.
                    continue;
                }

                if (tools.Count == message.Contents.Count)
                {
                    // Nothing was stripped, so the message is already the finished shape. Keep it whole,
                    // contents and order unchanged.
                    kept.Add(message);
                    continue;
                }

                ChatMessage trimmed = message.Clone();
                trimmed.Contents = tools;
                kept.Add(trimmed);
            }

            return kept;
        }

        /// <summary>Keeps only the tool calls that have a result, and those results, without the words beside them.</summary>
        /// <param name="messages">The messages of the turns a withdrawal takes, oldest first.</param>
        /// <returns>One message per message that held a kept call or result, in the original order.</returns>
        internal static List<ChatMessage> ToolPairs(IReadOnlyList<ChatMessage> messages)
        {
            HashSet<string> answered = [.. messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId)];
            HashSet<string> called = [.. messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId).Where(answered.Contains)];

            List<ChatMessage> pairs = [];
            foreach (ChatMessage message in messages)
            {
                List<AIContent> kept =
                [
                    .. message.Contents.Where(content => content switch
                    {
                        FunctionCallContent call => called.Contains(call.CallId),
                        FunctionResultContent result => called.Contains(result.CallId),
                        _ => false,
                    }),
                ];

                if (kept.Count > 0)
                {
                    pairs.Add(new ChatMessage(message.Role, kept) { AuthorName = message.AuthorName });
                }
            }

            return pairs;
        }

        /// <summary>
        /// The whole reply a turn's run produced, every step's text in order.
        /// </summary>
        /// <param name="messages">Every message the round produced.</param>
        /// <returns>
        /// Every step's text, concatenated with no separator between steps — the same rule
        /// <see cref="AgentCore.Application.Transcript.ShownWords.Lay"/> reconstructs a cut reply's shown text against, so a
        /// multi-step reply's hash and its transcript rows never disagree over where one step ends and
        /// the next begins.
        /// </returns>
        internal static string AllText(IList<ChatMessage> messages)
        {
            return string.Concat(FinishedMessages(messages).Select(message => message.Text));
        }
    }
}

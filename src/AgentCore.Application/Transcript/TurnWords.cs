using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>Decides the messages one sealed turn writes, by the Cut rule of design section 3.</summary>
    internal static class TurnWords
    {
        /// <summary>Builds the messages of one turn, the user's first.</summary>
        /// <param name="commit">The turn being sealed.</param>
        /// <param name="staged">Every message the framework staged for the turn, oldest first.</param>
        /// <returns>
        /// For a caller-facing turn: the line the user was shown, when there is one. For a cut turn: every step's
        /// words and finished tool pairs in the order they ran, the words cut to what was shown (see
        /// <see cref="ShownWords"/>). For a turn that failed without a cut: every step's shown words and finished tool
        /// pairs in the order they ran, then the fallback line as its own message, written once. Otherwise everything
        /// staged, or what the caller saw when nothing was staged.
        /// </returns>
        public static List<ChatMessage> Compose(TurnCommit commit, List<ChatMessage> staged)
        {
            List<ChatMessage> words = [commit.User];

            // A cancelled or faulted run stages nothing (probes P03, P06), so what the caller saw is the fuller record.
            IList<ChatMessage> source = commit.Seen?.Messages ?? staged;

            if (commit.CallerFacing)
            {
                string heard = commit.Reply ?? commit.Cut?.ShownText ?? ReplyText.From(source);
                if (heard.Length > 0)
                {
                    words.Add(new ChatMessage(ChatRole.Assistant, heard));
                }

                return words;
            }

            if (commit.Cut is null && commit.Completed)
            {
                words.AddRange(staged.Count > 0 ? staged : source);
                return words;
            }

            List<ChatMessage> finished = TurnMessages.FinishedMessages(source);
            string all = string.Concat(finished.Select(message => message.Text));

            if (commit.Cut is { } cut)
            {
                words.AddRange(ShownWords.Lay(finished, cut.ShownText ?? all).Where(Carries));
                return words;
            }

            // The fallback layer yields its line on the same stream, so the words seen end with it; it is laid
            // off the steps and written once, as its own message.
            string fallback = (commit.Reply ?? ReplyText.From(source)).Trim();
            string seen = all.TrimEnd();
            string before = fallback.Length > 0 && seen.EndsWith(fallback, StringComparison.Ordinal)
                ? seen[..^fallback.Length]
                : all;

            words.AddRange(ShownWords.Lay(finished, before).Where(Carries));
            if (fallback.Length > 0)
            {
                words.Add(new ChatMessage(ChatRole.Assistant, fallback));
            }

            return words;
        }

        /// <summary>
        /// Reads the words a turn's messages say, as the store's verify query rebuilds them from the rows: the text parts
        /// of every assistant message, in order, with no separator. The turn's <c>replyTextSha256</c> and
        /// <c>utteranceUntilInterruptSha256</c> hash this, so a hash and the rows it proves cannot drift apart.
        /// </summary>
        /// <param name="messages">The messages <see cref="Compose"/> built for the turn, or the rows written from them.</param>
        /// <returns>The words, or an empty string when no assistant message carries text.</returns>
        public static string Spoken(IEnumerable<ChatMessage> messages)
        {
            return string.Concat(messages
                .Where(message => message.Role == ChatRole.Assistant)
                .SelectMany(message => message.Contents.OfType<TextContent>())
                .Select(text => text.Text));
        }

        /// <summary>Whether a message still says or does anything once its words are cut.</summary>
        internal static bool Carries(ChatMessage message)
        {
            return message.Contents.Any(content => content
                is FunctionCallContent or FunctionResultContent or ToolApprovalRequestContent or TextContent { Text.Length: > 0 });
        }
    }
}

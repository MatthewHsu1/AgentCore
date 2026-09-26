using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>Lays the text a user was shown back over the messages of the reply that produced it.</summary>
    internal static class ShownWords
    {
        /// <summary>Cuts the text of <paramref name="messages"/> down to <paramref name="shown"/>, in order.</summary>
        /// <param name="messages">The messages of one reply, oldest first.</param>
        /// <param name="shown">Every word of the reply the user saw or heard, across all its steps.</param>
        /// <returns>
        /// One message per input, in the same order: the same instance when its text stands, a copy when its text
        /// was cut. Each message keeps its text while <paramref name="shown"/> still starts with it; the first that
        /// does not, or the last message with text, takes the rest, and every later message loses its text. When no
        /// message has text, the shown text is added as one assistant message at the end. Trimmed at both ends.
        /// </returns>
        public static List<ChatMessage> Lay(IList<ChatMessage> messages, string shown)
        {
            ArgumentNullException.ThrowIfNull(messages);
            ArgumentNullException.ThrowIfNull(shown);

            int last = -1;
            for (int index = 0; index < messages.Count; index++)
            {
                if (HasText(messages[index]))
                {
                    last = index;
                }
            }

            List<ChatMessage> laid = new(messages.Count + 1);
            string rest = shown;
            for (int index = 0; index < messages.Count; index++)
            {
                ChatMessage message = messages[index];
                if (!HasText(message))
                {
                    laid.Add(message);
                    continue;
                }

                string text = message.Text;
                if (index < last && rest.StartsWith(text, StringComparison.Ordinal))
                {
                    laid.Add(message);
                    rest = rest[text.Length..];
                    continue;
                }

                laid.Add(WithText(message, rest));
                rest = string.Empty;
            }

            if (last < 0 && shown.Length > 0)
            {
                laid.Add(new ChatMessage(ChatRole.Assistant, shown));
            }

            TrimEnds(laid);
            return laid;
        }

        private static void TrimEnds(List<ChatMessage> laid)
        {
            int first = laid.FindIndex(HasText);
            if (first >= 0)
            {
                laid[first] = WithText(laid[first], laid[first].Text.TrimStart());
            }

            int last = laid.FindLastIndex(HasText);
            if (last >= 0)
            {
                laid[last] = WithText(laid[last], laid[last].Text.TrimEnd());
            }
        }

        private static bool HasText(ChatMessage message)
        {
            return message.Contents.Any(content => content is TextContent { Text.Length: > 0 });
        }

        private static ChatMessage WithText(ChatMessage message, string text)
        {
            if (message.Text == text)
            {
                return message;
            }

            int at = message.Contents.ToList().FindIndex(content => content is TextContent);
            List<AIContent> contents = [.. message.Contents.Where(content => content is not TextContent)];
            if (text.Length > 0)
            {
                contents.Insert(Math.Max(at, 0), new TextContent(text));
            }

            ChatMessage cut = message.Clone();
            cut.Contents = contents;
            return cut;
        }
    }
}

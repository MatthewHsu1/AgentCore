namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// Splits a reply into <c>session.commentary.append</c> pieces. An append carries at most 500 tokens;
    /// 1,500 characters stays under that at the worst English ratio, about 3 characters per token.
    /// </summary>
    internal static class CommentaryPieces
    {
        internal const int MaxChars = 1500;

        internal static IReadOnlyList<string> Split(string text, int maxChars = MaxChars)
        {
            ArgumentNullException.ThrowIfNull(text);

            List<string> pieces = [];
            string rest = text.Trim();
            while (rest.Length > maxChars)
            {
                int cut = LastSentenceEnd(rest, maxChars);
                if (cut <= 0)
                {
                    cut = rest.LastIndexOf(' ', maxChars - 1);
                }

                if (cut <= 0)
                {
                    cut = maxChars;
                }

                pieces.Add(rest[..cut].TrimEnd());
                rest = rest[cut..].TrimStart();
            }

            if (rest.Length > 0)
            {
                pieces.Add(rest);
            }

            return pieces;
        }

        // The index of the space after the last ".", "?" or "!" within the first maxChars + 1 characters.
        private static int LastSentenceEnd(string text, int maxChars)
        {
            for (int index = Math.Min(maxChars, text.Length - 1); index > 0; index--)
            {
                if (text[index] is ' ' or '\n' && text[index - 1] is '.' or '?' or '!')
                {
                    return index;
                }
            }

            return -1;
        }
    }
}

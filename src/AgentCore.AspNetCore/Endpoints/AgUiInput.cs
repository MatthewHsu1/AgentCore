using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The AG-UI route's input rules : only the newest user message with text
    /// is read — history comes from the store — and what reaches the turn is words only.
    /// </summary>
    internal static class AgUiInput
    {
        /// <summary>Bytes of text one message may carry. Below Kestrel's 30 MB (spec rule 7).</summary>
        public const long MaxMessageChars = 5 * 1024 * 1024;

        public const string TooLargeCode = "message_too_large";

        /// <summary>Reads the newest user message that carries words, or null when no turn can run.</summary>
        public static ChatMessage? LastUserInput(IReadOnlyList<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);

            ChatMessage? picked = null;
            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.User && message.Text is { Length: > 0 })
                {
                    picked = message;
                }
            }

            return picked;
        }

        /// <summary>Checks the picked message against the per-message size cap.</summary>
        /// <returns>The refusal, or null when the message may run.</returns>
        public static (int Status, string Code, string Message)? CheckSize(ChatMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            long chars = 0;
            foreach (AIContent content in message.Contents)
            {
                if (content is TextContent text)
                {
                    chars += text.Text.Length;
                }
            }

            return chars > MaxMessageChars
                ? (400, TooLargeCode, $"a message carries at most {MaxMessageChars} characters of text; this one carries {chars}.")
                : null;
        }

        /// <summary>
        /// Sanitizes the picked message for the turn: clears the author name and drops every
        /// non-text part (URL-source media never reaches the model; data parts wait for Phase 3 filing).
        /// </summary>
        public static ChatMessage Sanitize(ChatMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            List<AIContent> kept = [.. message.Contents.OfType<TextContent>()];
            ChatMessage sanitized = new(message.Role, kept)
            {
                MessageId = message.MessageId,
                CreatedAt = message.CreatedAt,
                AdditionalProperties = message.AdditionalProperties,
                RawRepresentation = message.RawRepresentation,
            };
            return sanitized;
        }
    }
}

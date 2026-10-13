using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The AG-UI route's input rules: only the newest user message with words or a file
    /// is read — history comes from the store — and what reaches the turn is words plus
    /// the caller's files (filed to the blob store downstream, re-resolved downstream).
    /// </summary>
    internal static class AgUiInput
    {
        /// <summary>Bytes of text one message may carry. Below Kestrel's 30 MB (spec rule 7).</summary>
        public const long MaxMessageChars = 5 * 1024 * 1024;

        public const string TooLargeCode = "message_too_large";

        /// <summary>Reads the newest user message that carries words or a file, or null when no turn can run.</summary>
        public static ChatMessage? LastUserInput(IReadOnlyList<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);

            return AgUiAttachments.LastUserInput(messages);
        }

        /// <summary>Checks the picked message against the per-message size cap.</summary>
        /// <returns>The refusal, or null when the message may run.</returns>
        public static (int Status, string Code, string Message)? CheckSize(ChatMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            long size = 0;
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        size += text.Text.Length;
                        break;
                    case DataContent data:
                        size += data.Data.Length;
                        break;
                }
            }

            return size > MaxMessageChars
                ? (400, TooLargeCode, $"a message carries at most {MaxMessageChars} characters of text and bytes of attachments; this one carries {size}.")
                : null;
        }

        /// <summary>
        /// Sanitizes the picked message for the turn: clears the author name and keeps text plus the
        /// caller's files (data parts are filed downstream; URL parts are resolved or refused downstream;
        /// file parts are the stored files resolve and the regenerate dedupe already restored).
        /// </summary>
        public static ChatMessage Sanitize(ChatMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            List<AIContent> kept = [.. message.Contents.Where(content => content is TextContent or DataContent or UriContent or FileContent)];
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

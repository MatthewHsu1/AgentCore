using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// The content types AgentCore adds to a message for the person reading the transcript, not for
    /// the model: a drawing, a citation, a published file. This is the one list of them. It decides
    /// how a store encodes them and what is stripped before a message reaches the model.
    /// </summary>
    internal static class ReaderContent
    {
        /// <summary>The discriminator a stored RenderContent is written with. It is a wire format.</summary>
        private const string RenderContentTypeId = "agentcore.render";

        /// <summary>The discriminator a stored SourceContent is written with. It is a wire format.</summary>
        private const string SourceContentTypeId = "agentcore.source";

        /// <summary>The discriminator a stored FileContent is written with. It is a wire format.</summary>
        private const string FileContentTypeId = "agentcore.file";

        /// <summary>Whether <paramref name="content"/> is for the reader and not the model.</summary>
        /// <param name="content">The content to test.</param>
        public static bool Is(AIContent content)
        {
            return content is RenderContent or SourceContent or FileContent;
        }

        /// <summary>Teaches <paramref name="options"/> to encode and decode every reader content type.</summary>
        /// <param name="options">A writable options instance.</param>
        public static void Register(JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            options.AddAIContentType<RenderContent>(RenderContentTypeId);
            options.AddAIContentType<SourceContent>(SourceContentTypeId);
            options.AddAIContentType<FileContent>(FileContentTypeId);
        }

        /// <summary>
        /// The same messages with every reader content removed. A message that holds none is returned
        /// as-is; one that does is copied, so the transcript's own instance keeps what it had.
        /// </summary>
        /// <param name="messages">The messages bound for the model.</param>
        public static IEnumerable<ChatMessage> Strip(IEnumerable<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);

            return messages.Select(Strip);
        }

        private static ChatMessage Strip(ChatMessage message)
        {
            return !message.Contents.Any(Is)
                ? message
                : new ChatMessage(message.Role, [.. message.Contents.Where(content => !Is(content))])
                {
                    AuthorName = message.AuthorName,
                    MessageId = message.MessageId,
                    CreatedAt = message.CreatedAt,
                    AdditionalProperties = message.AdditionalProperties,
                    RawRepresentation = message.RawRepresentation,
                };
        }
    }
}

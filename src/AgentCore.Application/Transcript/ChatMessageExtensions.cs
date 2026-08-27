using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>Helpers for editing a <see cref="ChatMessage"/>'s contents without losing the rest of it.</summary>
internal static class ChatMessageExtensions
{
    /// <summary>Returns the message with every <see cref="RenderContent"/> removed.</summary>
    public static ChatMessage WithoutRenders(this ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!message.Contents.Any(static content => content is RenderContent))
        {
            return message;
        }

        // ChatMessage.Clone()'s own list of what a shallow copy must carry, minus Contents (rebuilt
        // below) and Role (passed to the constructor): AdditionalProperties, MessageId, AuthorName,
        // CreatedAt, RawRepresentation.
        return new ChatMessage(message.Role, [.. message.Contents.Where(static content => content is not RenderContent)])
        {
            AdditionalProperties = message.AdditionalProperties,
            MessageId = message.MessageId,
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            RawRepresentation = message.RawRepresentation,
        };
    }
}

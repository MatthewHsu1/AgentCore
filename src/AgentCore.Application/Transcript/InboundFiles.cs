using AgentCore.Application.Blobs;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>
/// Files the caller attached. On arrival each file's bytes go to the blob store under the conversation, and the
/// message keeps a <see cref="FileContent"/> that names the blob, so a stored row never holds the bytes. The model
/// still gets the bytes: <see cref="RehydrateAsync"/> reads them back each time the model is called.
/// </summary>
public static class InboundFiles
{
    /// <summary>What <see cref="FileContent.FileId"/> starts with on a file the caller sent, followed by the caller's file name.</summary>
    public const string FileIdPrefix = "upload:";

    /// <summary>Whether a file reference names a file the caller sent, not one the reply published.</summary>
    /// <param name="file">The reference.</param>
    public static bool Is(FileContent file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return file.Kept && file.FileId.StartsWith(FileIdPrefix, StringComparison.Ordinal);
    }

    /// <summary>Writes every <see cref="DataContent"/> of a message to the store and returns the message with references in their place.</summary>
    /// <param name="blobs">The store.</param>
    /// <param name="conversationId">The owner the blobs are written under; deleting the conversation deletes them.</param>
    /// <param name="message">The caller's message.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The same instance when it carries no bytes; otherwise a copy.</returns>
    public static async ValueTask<ChatMessage> FileAsync(IBlobStore blobs, string conversationId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(message);

        if (!message.Contents.Any(content => content is DataContent))
        {
            return message;
        }

        List<AIContent> contents = [];
        foreach (AIContent content in message.Contents)
        {
            if (content is not DataContent data)
            {
                contents.Add(content);
                continue;
            }

            string name = "in-" + Guid.NewGuid().ToString("N")[..16] + ExtensionOf(data.MediaType);
            byte[] bytes = data.Data.ToArray();
            using MemoryStream stream = new(bytes, writable: false);
            BlobRef blob = await blobs.PutAsync(new BlobWrite(conversationId, name, data.MediaType, stream, bytes.Length), cancellationToken).ConfigureAwait(false);
            contents.Add(new FileContent
            {
                Name = blob.Name,
                FileId = FileIdPrefix + (data.Name ?? string.Empty),
                Title = data.Name,
                MediaType = blob.MediaType,
                Length = blob.Length,
                Kept = true,
            });
        }

        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt,
            AdditionalProperties = message.AdditionalProperties,
        };
    }

    /// <summary>
    /// The same messages with each reference to a caller's file replaced by its bytes, read from the store. A file the
    /// store no longer holds becomes a line of text saying so. Messages without one are returned as they are.
    /// </summary>
    /// <param name="blobs">The store.</param>
    /// <param name="conversationId">The owner the blobs were written under.</param>
    /// <param name="messages">The messages bound for the model.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async ValueTask<List<ChatMessage>> RehydrateAsync(
        IBlobStore blobs, string conversationId, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(messages);

        List<ChatMessage> result = [];
        foreach (ChatMessage message in messages)
        {
            if (!message.Contents.Any(content => content is FileContent file && Is(file)))
            {
                result.Add(message);
                continue;
            }

            List<AIContent> contents = [];
            foreach (AIContent content in message.Contents)
            {
                if (content is not FileContent file || !Is(file))
                {
                    contents.Add(content);
                    continue;
                }

                await using BlobRead? read = await blobs.OpenReadAsync(conversationId, file.Name, cancellationToken).ConfigureAwait(false);
                if (read is null)
                {
                    contents.Add(new TextContent($"[the attachment '{file.Title ?? file.Name}' is no longer stored]"));
                    continue;
                }

                using MemoryStream copy = new();
                await read.Content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                contents.Add(new DataContent(copy.ToArray(), file.MediaType) { Name = file.Title });
            }

            result.Add(new ChatMessage(message.Role, contents)
            {
                AuthorName = message.AuthorName,
                MessageId = message.MessageId,
                CreatedAt = message.CreatedAt,
                AdditionalProperties = message.AdditionalProperties,
                RawRepresentation = message.RawRepresentation,
            });
        }

        return result;
    }

    private static string ExtensionOf(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        _ => string.Empty,
    };
}

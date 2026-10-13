using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// What a run's newest user message may carry. The limits guard the HTTP door and differ per
/// host, not per agent.
/// </summary>
internal sealed record AgUiAttachmentLimits
{
    /// <summary>Bytes one decoded part may hold.</summary>
    public long MaxPartBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Bytes all decoded parts of one message may hold together.</summary>
    public long MaxMessageBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>How many non-text parts one message may carry.</summary>
    public int MaxParts { get; init; } = 4;

    /// <summary>Media types a part may declare. Matched exactly, case-insensitive.</summary>
    public IReadOnlyList<string> AllowedMediaTypes { get; init; } = ["image/png", "image/jpeg", "image/webp", "image/gif", "application/pdf"];

    /// <summary>Whether a part may name a URL instead of carrying its bytes. Off: the model vendor would fetch it.</summary>
    public bool AllowUrlSources { get; init; }
}

/// <summary>Picks the run's newest user input and checks its attachments before any turn starts.</summary>
internal static class AgUiAttachments
{
    public const string TooLarge = "attachment_too_large";
    public const string TypeNotAllowed = "attachment_type_not_allowed";
    public const string UrlNotAllowed = "attachment_url_not_allowed";
    public const string TooMany = "attachment_too_many";

    /// <summary>The last user message that carries words or a file; an image alone is a turn.</summary>
    public static ChatMessage? LastUserInput(IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage? picked = null;
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.User
                && (message.Text is { Length: > 0 } || message.Contents.Any(c => c is DataContent or UriContent)))
            {
                picked = message;
            }
        }

        return picked;
    }

    /// <summary>Why the message is refused, or null when it may run.</summary>
    public static (int Status, string Code, string Message)? Check(ChatMessage message, AgUiAttachmentLimits limits)
    {
        long total = 0;
        int parts = 0;
        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case UriContent uri when !limits.AllowUrlSources:
                    return (400, UrlNotAllowed, $"an attachment must carry its bytes; '{uri.Uri}' is a link.");
                case UriContent uri when !Allowed(uri.MediaType, limits):
                    return (415, TypeNotAllowed, $"'{uri.MediaType}' is not an accepted attachment type.");
                case DataContent data when !Allowed(data.MediaType, limits):
                    return (415, TypeNotAllowed, $"'{data.MediaType}' is not an accepted attachment type.");
                case DataContent data when data.Data.Length > limits.MaxPartBytes:
                    return (413, TooLarge, $"'{data.Name ?? data.MediaType}' is {data.Data.Length} bytes; the limit is {limits.MaxPartBytes}.");
                case DataContent data:
                    total += data.Data.Length;
                    parts++;
                    break;
                case UriContent:
                    parts++;
                    break;
            }
        }

        if (parts > limits.MaxParts)
        {
            return (400, TooMany, $"a message carries at most {limits.MaxParts} attachments; this one carries {parts}.");
        }

        return total > limits.MaxMessageBytes
            ? (413, TooLarge, $"the attachments total {total} bytes; the limit is {limits.MaxMessageBytes}.")
            : null;
    }

    private static bool Allowed(string mediaType, AgUiAttachmentLimits limits)
        => limits.AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
}

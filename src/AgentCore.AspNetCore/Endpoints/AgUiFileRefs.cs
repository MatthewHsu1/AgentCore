using AgentCore.Application.Blobs;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The one reference the browser holds for a stored file: the absolute URL of the host's file
/// door, <c>{origin}{pathBase}/agui/threads/{threadId}/files/{blobName}</c>. It carries no
/// secret and never expires. The browser swaps it for a fresh link when it draws the file
/// (<c>GET {reference}/link</c>), and sends it back as it is on a regenerate or an edit, where
/// <see cref="ResolveAsync"/> turns it into the stored file again.
/// </summary>
internal static class AgUiFileRefs
{
    /// <summary>The reference names a file of another conversation.</summary>
    public const string NotOwned = "attachment_not_owned";

    /// <summary>The reference is shaped right but names no file this conversation kept.</summary>
    public const string NotFound = "attachment_not_found";

    /// <summary>The reference's file name is not a plain blob name (a path, <c>..</c>, a control character).</summary>
    public const string Invalid = "attachment_ref_invalid";

    public static string PathOf(string threadId, string name)
        => $"/agui/threads/{Uri.EscapeDataString(threadId)}/files/{Uri.EscapeDataString(name)}";

    /// <summary>The reference to one file, absolute on the origin the request came in on.</summary>
    public static string RefOf(HttpRequest request, string threadId, string name)
        => $"{request.Scheme}://{request.Host}{request.PathBase}{PathOf(threadId, name)}";

    /// <summary>
    /// Whether a URL is shaped like a reference: an http(s) or root-relative URL whose path is exactly
    /// <c>{pathBase}/agui/threads/{threadId}/files/{name}</c>. The origin is not compared: the reference is only ever
    /// looked up in this host's own store, so a foreign origin can name nothing but this host's files.
    /// </summary>
    public static bool TryRead(Uri uri, PathString pathBase, out string threadId, out string name)
    {
        threadId = name = string.Empty;
        string path;
        if (uri.IsAbsoluteUri)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            path = uri.AbsolutePath;
        }
        else
        {
            path = uri.OriginalString.Split('?', '#')[0];
        }

        if (pathBase.HasValue)
        {
            if (!path.StartsWith(pathBase.Value!, StringComparison.Ordinal))
            {
                return false;
            }

            path = path[pathBase.Value!.Length..];
        }

        string[] segments = path.Split('/');
        if (segments is not ["", "agui", "threads", { Length: > 0 } thread, "files", { Length: > 0 } file])
        {
            return false;
        }

        threadId = Uri.UnescapeDataString(thread);
        name = Uri.UnescapeDataString(file);
        return true;
    }

    /// <summary>
    /// Puts the stored file back in place of each reference the message carries. A reference is honoured only when it
    /// names this conversation, a plain blob name, and a file one of this conversation's rows kept; anything else
    /// refuses the run before a turn starts. A link that is not a reference is left alone for the attachment check.
    /// </summary>
    public static async Task<(ChatMessage Message, (int Status, string Code, string Message)? Refused)> ResolveAsync(
        ChatMessage words, string threadId, PathString pathBase, IConversations conversations, CancellationToken ct)
    {
        if (!words.Contents.Any(c => c is UriContent))
        {
            return (words, null);
        }

        StoredConversation? stored = null;
        List<AIContent> contents = [];
        bool changed = false;
        foreach (AIContent content in words.Contents)
        {
            if (content is not UriContent uri || !TryRead(uri.Uri, pathBase, out string owner, out string name))
            {
                contents.Add(content);
                continue;
            }

            if (!string.Equals(owner, threadId, StringComparison.Ordinal))
            {
                return (words, (403, NotOwned, "the attachment names a file of another conversation."));
            }

            if (!BlobName.IsSafe(name))
            {
                return (words, (400, Invalid, "the attachment's file name is not a plain name."));
            }

            stored ??= await conversations.LoadWindowAsync(threadId, new TranscriptWindow(null, int.MaxValue), ct).ConfigureAwait(false);
            if (KeptFile(stored, name) is not { } file)
            {
                return (words, (404, NotFound, $"this conversation kept no file named '{name}'."));
            }

            contents.Add(InboundFiles.Is(file)
                ? file
                : new FileContent
                {
                    Name = file.Name,
                    FileId = InboundFiles.FileIdPrefix + file.Name,
                    Title = file.Name,
                    MediaType = file.MediaType,
                    Length = file.Length,
                    Kept = true,
                });
            changed = true;
        }

        return changed
            ? (new ChatMessage(words.Role, contents)
            {
                AuthorName = words.AuthorName,
                MessageId = words.MessageId,
                CreatedAt = words.CreatedAt,
                AdditionalProperties = words.AdditionalProperties,
                RawRepresentation = words.RawRepresentation,
            }, null)
            : (words, null);
    }

    /// <summary>The newest kept file a row of the conversation names, or null.</summary>
    private static FileContent? KeptFile(StoredConversation? stored, string name)
        => stored?.Messages.SelectMany(r => r.Content.Contents.OfType<FileContent>())
            .LastOrDefault(f => f.Kept && string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>The name a download is saved under: the caller's own file name for an upload, the blob name otherwise.</summary>
    private static async Task<string> DownloadNameAsync(IConversations conversations, string threadId, string name, CancellationToken ct)
    {
        StoredConversation? stored = await conversations.LoadWindowAsync(threadId, new TranscriptWindow(null, int.MaxValue), ct).ConfigureAwait(false);
        return KeptFile(stored, name) is { } file && InboundFiles.Is(file) && !string.IsNullOrWhiteSpace(file.Title) ? file.Title : name;
    }

    /// <summary>How long the links the link route mints live.</summary>
    public static TimeSpan LinkLifetime { get; } = BlobLink.Lifetime;

    /// <summary>
    /// <c>GET /agui/threads/{threadId}/files/{name}/link</c>: a fresh link to one stored file, and when it stops working.
    /// A store with no web door answers the reference itself, which the door below serves.
    /// Behind the G-7 ownership hook, like every other thread route.
    /// </summary>
    public static IEndpointConventionBuilder MapAgUiFileLink(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The door is global, not per entry: the reference names no entry, and entry resolution runs per
        // request inside the handler (a BeforeEntry hook still picks the entry for the ownership gate).
        // No AgUi route metadata: the startup entry check only walks tagged routes.
        return endpoints.MapGet("/agui/threads/{threadId}/files/{name}/link", async (
            HttpContext http, string threadId, string name, IConversations conversations, CancellationToken ct) =>
        {
            if (!await OwnedAsync(http, threadId, ct).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            if (http.RequestServices.GetService<IBlobStore>() is not { } blobs || !BlobOwner.IsSafe(threadId) || !BlobName.IsSafe(name)
                || await blobs.StatAsync(threadId, name, ct).ConfigureAwait(false) is not { } blob)
            {
                return Results.NotFound();
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            Uri? url = await blobs.LinkAsync(blob with { DownloadName = await DownloadNameAsync(conversations, threadId, name, ct).ConfigureAwait(false) }, LinkLifetime, ct).ConfigureAwait(false);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(url is null
                ? new { url = RefOf(http.Request, threadId, name), expiresAt = (long?)null }
                : new { url = url.ToString(), expiresAt = (long?)(now + LinkLifetime).ToUnixTimeMilliseconds() });
        });
    }

    /// <summary>
    /// <c>GET /agui/threads/{threadId}/files/{name}</c>: the reference itself, so a client that draws it without a swap
    /// still gets the file. A store with a web door redirects to a fresh link; one without streams the bytes.
    /// Either way the download keeps the caller's file name. Behind the G-7 ownership hook.
    /// </summary>
    public static IEndpointConventionBuilder MapAgUiFileDoor(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Global like the link route above: the reference names no entry.
        return endpoints.MapGet("/agui/threads/{threadId}/files/{name}", async (
            HttpContext http, string threadId, string name, IConversations conversations, CancellationToken ct) =>
        {
            if (!await OwnedAsync(http, threadId, ct).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            if (http.RequestServices.GetService<IBlobStore>() is not { } blobs || !BlobOwner.IsSafe(threadId) || !BlobName.IsSafe(name)
                || await blobs.StatAsync(threadId, name, ct).ConfigureAwait(false) is not { } blob)
            {
                return Results.NotFound();
            }

            string download = await DownloadNameAsync(conversations, threadId, name, ct).ConfigureAwait(false);
            if (await blobs.LinkAsync(blob with { DownloadName = download }, LinkLifetime, ct).ConfigureAwait(false) is { } url)
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.Redirect(url.ToString());
            }

            if (await blobs.OpenReadAsync(threadId, name, ct).ConfigureAwait(false) is not { } read)
            {
                return Results.NotFound();
            }
            http.Response.Headers.ContentDisposition = BlobLink.DispositionOf(read.MediaType, download);
            return Results.Stream(read.Content, read.MediaType);
        });
    }

    /// <summary>Runs the G-7 ownership hook for one thread-scoped request: unknown or refused reads as not found.</summary>
    internal static async ValueTask<bool> OwnedAsync(HttpContext http, string threadId, CancellationToken ct)
    {
        string? entry = await AgentCoreEntries.ResolveAsync(http, ct).ConfigureAwait(false);
        if (entry is null && http.RequestServices.GetRequiredService<AgentCoreBoot>().Entries.Entries is { Count: 1 } declared)
        {
            entry = declared.First();
        }

        if (entry is null)
        {
            return false;
        }

        if (await http.RequestServices.GetRequiredService<IConversations>().GetAsync(threadId, ct).ConfigureAwait(false) is null)
        {
            return false;
        }

        return await AgentCoreConversationGate.AllowAsync(http, entry, threadId, resuming: false, ct).ConfigureAwait(false);
    }
}

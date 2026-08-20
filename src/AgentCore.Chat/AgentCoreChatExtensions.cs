using System.Text;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace AgentCore.Chat;

/// <summary>
/// Serves the chat UI beside the endpoint it talks to.
/// </summary>
/// <remarks>
/// <para>
/// The UI is a browser application and no part of it runs here: this file serves files and nothing
/// else. The conversation lives in the browser, and the call lives in AgentCore. What joins them is
/// the <c>X-AgentCore-Session</c> header, and the browser is what carries it — see
/// <c>ClientApp/src/AgentCoreRuntime.ts</c>. That is why this host holds no map from a chat thread
/// to a call, and why nothing here has to be told when a call ends.
/// </para>
/// <para>
/// The files are embedded in this assembly rather than copied beside the exe, so a consumer gets
/// the UI by referencing the project and nothing else.
/// </para>
/// </remarks>
public static class AgentCoreChatExtensions
{
    /// <summary>The path the UI is served on.</summary>
    /// <remarks>
    /// This is not a parameter. The bundler writes this prefix into every asset URL while the UI is
    /// built, so a host that moved the path would get a page whose script and stylesheet both 404. A
    /// host that needs another path changes <c>base</c> in <c>ClientApp/vite.config.ts</c> and
    /// rebuilds.
    /// </remarks>
    public const string UiPath = "/chat";

    /// <summary>The attribute the page reads its endpoint from, as the bundler wrote it.</summary>
    private const string EndpointAttribute = "data-agentcore-endpoint=\"";

    /// <summary>The folder inside this assembly the files were embedded from.</summary>
    private const string EmbeddedRoot = "wwwroot";

    /// <summary>The page itself, which is the one file that is rewritten rather than served whole.</summary>
    private const string IndexFile = "index.html";

    /// <summary>What an asset is answered with.</summary>
    /// <remarks>
    /// The asset names are stable rather than content-hashed — see the comment in
    /// <c>AgentCore.Chat.csproj</c> for why — so a name says nothing about whether the bytes behind
    /// it changed. The browser is told to check every time instead, and <c>Last-Modified</c> makes
    /// that check a conditional request that answers 304 while the deployment stands still.
    /// </remarks>
    private const string RevalidateCacheControl = "no-cache";

    /// <summary>Serves the chat UI on <see cref="UiPath"/>.</summary>
    /// <param name="app">The application to map on.</param>
    /// <param name="chatCompletionsPattern">
    /// The route the page should post turns to, or <see langword="null"/> for
    /// <see cref="ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern"/>. Pass the same
    /// value given to <c>MapAgentCoreHost</c>: a host that moved the endpoint and did not say so
    /// here would serve a page that posts into a 404.
    /// </param>
    /// <returns>The same application, so a host chains its calls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The UI was not embedded in this assembly, which means the build ran with an empty
    /// <c>wwwroot</c>.
    /// </exception>
    /// <remarks>
    /// One endpoint answers both the assets and the page, rather than the usual pairing of
    /// <c>UseStaticFiles</c> with a fallback route. The static file middleware stands down as soon
    /// as routing has selected an endpoint, and the fallback this UI needs — a deep link under
    /// <see cref="UiPath"/> has to reach the page rather than a 404 — is exactly such an endpoint.
    /// The two together serve the page in place of every script and stylesheet the page asks for.
    /// Looking the file up here instead is what makes the order stop mattering.
    /// </remarks>
    public static WebApplication MapAgentCoreChat(this WebApplication app, string? chatCompletionsPattern = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var assembly = typeof(AgentCoreChatExtensions).Assembly;
        ManifestEmbeddedFileProvider files = new(assembly, EmbeddedRoot);

        if (files.GetFileInfo(IndexFile) is not { Exists: true } index)
        {
            throw new InvalidOperationException(
                $"AgentCore.Chat holds no embedded '{IndexFile}'. The UI is built from ClientApp into "
                + "wwwroot and that folder is committed, so an assembly without it was built from a "
                + "tree where the build output was deleted. Run 'npm run build' in "
                + "src/AgentCore.Chat/ClientApp and build again.");
        }

        // Read and rewrite once, while the host starts. The page is a few hundred bytes and never
        // changes after this, so serving it from a string costs one allocation for the life of the
        // process and no file read per request.
        var page = Rewrite(
            index,
            chatCompletionsPattern ?? ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern);

        FileExtensionContentTypeProvider contentTypes = new();

        app.MapGet(UiPath, () => Page(page));
        app.MapGet($"{UiPath}/{{*rest}}", (HttpContext http, string? rest) =>
            Asset(http, files, contentTypes, rest) ?? Page(page));

        return app;
    }

    /// <summary>Answers one embedded asset, or nothing when the path names none.</summary>
    /// <param name="http">The request, whose response headers carry the cache policy.</param>
    /// <param name="files">The embedded files.</param>
    /// <param name="contentTypes">Maps a file extension to what the browser should read it as.</param>
    /// <param name="rest">The path under <see cref="UiPath"/>.</param>
    /// <returns>The file, or <see langword="null"/> when the path is a deep link rather than a file.</returns>
    private static IResult? Asset(
        HttpContext http,
        ManifestEmbeddedFileProvider files,
        FileExtensionContentTypeProvider contentTypes,
        string? rest)
    {
        if (rest is not { Length: > 0 })
        {
            return null;
        }

        if (files.GetFileInfo(rest) is not { Exists: true, IsDirectory: false } file)
        {
            return null;
        }

        if (!contentTypes.TryGetContentType(rest, out var contentType))
        {
            // An extension nothing claims is still a file the page asked for, and a browser reads an
            // unknown type as bytes rather than guessing.
            contentType = "application/octet-stream";
        }

        // A stale bundle behind a name that never changes is what a deployment would otherwise
        // leave in every open browser, so the browser revalidates rather than trusting the name.
        http.Response.Headers.CacheControl = RevalidateCacheControl;

        return Results.File(file.CreateReadStream(), contentType, lastModified: file.LastModified);
    }

    /// <summary>Points the page at the route this host actually mapped.</summary>
    /// <param name="index">The embedded page.</param>
    /// <param name="pattern">The route the page should post to.</param>
    /// <returns>The page to serve.</returns>
    /// <remarks>
    /// The bundler writes the default into the attribute, so the replacement is a plain swap of one
    /// quoted value. A pattern equal to the default leaves the page exactly as it was built.
    /// </remarks>
    private static string Rewrite(IFileInfo index, string pattern)
    {
        using var stream = index.CreateReadStream();
        using StreamReader reader = new(stream, Encoding.UTF8);
        var html = reader.ReadToEnd();

        var start = html.IndexOf(EndpointAttribute, StringComparison.Ordinal);
        if (start < 0)
        {
            // The page carries no attribute, which means the UI was built from a tree where it was
            // removed. It falls back to the default at runtime, so serving it whole is right.
            return html;
        }

        var from = start + EndpointAttribute.Length;
        var to = html.IndexOf('"', from);
        return to < 0 ? html : string.Concat(html.AsSpan(0, from), pattern, html.AsSpan(to));
    }

    /// <summary>Answers the page.</summary>
    /// <param name="page">The rewritten page.</param>
    /// <returns>The result.</returns>
    /// <remarks>
    /// No caching: the asset names carry a content hash and are cached for a year above, and the
    /// page is what names them. A cached page would survive a deployment and ask for assets that
    /// are gone.
    /// </remarks>
    private static IResult Page(string page)
        => Results.Text(page, "text/html", Encoding.UTF8);
}

using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.AspNetCore.Conversation;

/// <summary>
/// Maps the inbound conversation route, with the URL naming the entry, onto whichever transport the
/// document names.
/// </summary>
public static class ConversationEndpointRouteBuilderExtensions
{
    /// <summary>The route parameter that names the entry, as <c>{entry}</c> in a pattern.</summary>
    public const string EntryRouteParameter = "entry";

    /// <summary>The route every entry answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/{" + EntryRouteParameter + "}/call";

    /// <summary>Maps every entry on <see cref="DefaultPattern"/>, with the URL naming the entry.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapCall(this IEndpointRouteBuilder endpoints)
        => endpoints.MapCall(DefaultPattern);

    /// <summary>Maps every entry on one route, with the URL naming the entry.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on. It must carry the <c>{entry}</c> parameter.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> carries no <c>{entry}</c>.</exception>
    public static IEndpointConventionBuilder MapCall(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        if (!pattern.Contains("{" + EntryRouteParameter + "}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The pattern '{pattern}' carries no {{{EntryRouteParameter}}} parameter, so no URL can name "
                + "an entry.",
                nameof(pattern));
        }

        // Map, and not MapGet. An HTTP/2 WebSocket arrives as CONNECT rather than GET, and MapGet
        // would answer 405 to it.
        return endpoints.Map(pattern, (HttpContext http) => DispatchAsync(http, pattern));
    }

    /// <summary>Reads the entry the URL names, off the <c>{entry}</c> route parameter.</summary>
    /// <param name="http">The request on a conversation route.</param>
    /// <returns>The entry key, or the empty string when the route carries none.</returns>
    public static string EntryOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.RouteValues[EntryRouteParameter] as string ?? string.Empty;
    }

    private static Task DispatchAsync(HttpContext http, string pattern)
    {
        // GetService and never GetRequiredService, on purpose. A host may map this route with no
        // AgentCore registration at all, and such a host must get a readable reason rather than a
        // resolution failure.
        if (http.RequestServices.GetService<AgentCoreBoot>() is not { } boot)
        {
            return NotRoutedAsync(http, pattern, "this host registered no AgentCore services");
        }

        if (boot.ConversationHandler is not { } handler)
        {
            return NotRoutedAsync(http, pattern, boot.ConversationUnroutable ?? "this host routes no inbound conversation");
        }

        var entry = EntryOf(http);

        return boot.Entries.ConversationSessions.ContainsKey(entry)
            ? handler(http)
            : UnknownEntryAsync(http, EntryRegistry.UnknownEntryMessage(entry, boot.Entries.Entries));
    }

    private static async Task NotRoutedAsync(HttpContext http, string pattern, string reason)
    {
        var logger = (http.RequestServices.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger(typeof(ConversationEndpointRouteBuilderExtensions).FullName!);

        ConversationRouteLog.RouteNotMapped(logger, pattern, reason);

        await WriteProblemAsync(http, StatusCodes.Status503ServiceUnavailable, "This host routes no inbound conversation.", reason, pattern)
            .ConfigureAwait(false);
    }

    private static Task UnknownEntryAsync(HttpContext http, string reason)
        => WriteProblemAsync(http, StatusCodes.Status404NotFound, "No entry answers on this route.", reason, http.Request.Path);

    private static async Task WriteProblemAsync(HttpContext http, int status, string title, string detail, string instance)
    {
        http.Response.StatusCode = status;

        await http.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = instance,
        }, http.RequestAborted).ConfigureAwait(false);
    }
}

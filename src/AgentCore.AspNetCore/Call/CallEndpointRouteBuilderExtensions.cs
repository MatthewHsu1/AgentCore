using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// Maps one inbound call route per entry, onto whichever transport the document names.
/// </summary>
public static class CallEndpointRouteBuilderExtensions
{
    /// <summary>The route the call transport answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/call";

    /// <summary>Maps the inbound call route for one entry on <see cref="DefaultPattern"/>.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="entry">The entry key this route answers on.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapCall(this IEndpointRouteBuilder endpoints, string entry)
        => endpoints.MapCall(DefaultPattern, entry);

    /// <summary>Maps the inbound call route for one entry on one route.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <param name="entry">The entry key this route answers on.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapCall(
        this IEndpointRouteBuilder endpoints, string pattern, string entry)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentException.ThrowIfNullOrEmpty(entry);

        // Map, and not MapGet. An HTTP/2 WebSocket arrives as CONNECT rather than GET, and MapGet
        // would answer 405 to it.
        return endpoints.Map(pattern, (HttpContext http) => DispatchAsync(http, pattern, entry))
            .WithMetadata(new AgentCoreEntryMetadata(entry, "Call"));
    }

    private static Task DispatchAsync(HttpContext http, string pattern, string entry)
    {
        // GetService and never GetRequiredService, on purpose. A host may map this route with no
        // AgentCore registration at all, and such a host must get a readable reason rather than a
        // resolution failure.
        if (http.RequestServices.GetService<AgentCoreBoot>() is not { } boot)
        {
            return NotRoutedAsync(http, pattern, "this host registered no AgentCore services");
        }

        if (boot.CallHandlers is not { } handlers)
        {
            return NotRoutedAsync(http, pattern, boot.CallUnroutable ?? "this host routes no inbound call");
        }

        return handlers.TryGetValue(entry, out var handler)
            ? handler(http)
            : NotRoutedAsync(http, pattern, EntryRegistry.UnknownEntryMessage(entry, handlers.Keys));
    }

    private static async Task NotRoutedAsync(HttpContext http, string pattern, string reason)
    {
        var logger = (http.RequestServices.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger(typeof(CallEndpointRouteBuilderExtensions).FullName!);

        CallRouteLog.RouteNotMapped(logger, pattern, reason);

        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        await http.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "This host routes no inbound call.",
            Detail = reason,
            Instance = pattern,
        }, http.RequestAborted).ConfigureAwait(false);
    }
}

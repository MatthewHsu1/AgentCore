using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// Maps the one inbound call route, onto whichever transport the document names.
/// </summary>
public static class CallEndpointRouteBuilderExtensions
{
    /// <summary>The route the call transport answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/call";

    /// <summary>Maps the inbound call route on <see cref="DefaultPattern"/>.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapCall(this IEndpointRouteBuilder endpoints)
        => endpoints.MapCall(DefaultPattern);

    /// <summary>Maps the inbound call route on one route.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapCall(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        // Map, and not MapGet. An HTTP/2 WebSocket arrives as CONNECT rather than GET, and MapGet
        // would answer 405 to it.
        return endpoints.Map(pattern, (HttpContext http) => DispatchAsync(http, pattern));
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

        return boot.CallHandler is { } handler
            ? handler(http)
            : NotRoutedAsync(http, pattern, boot.CallUnroutable ?? "this host routes no inbound call");
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
        }).ConfigureAwait(false);
    }
}

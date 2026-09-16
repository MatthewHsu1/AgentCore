using AgentCore.AspNetCore.Call;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentCore.Hosting;

/// <summary>
/// Maps every route AgentCore answers on, in one call.
/// </summary>
public static class AgentCoreHostEndpointExtensions
{
    /// <summary>The route the liveness check answers on.</summary>
    public const string HealthPattern = "/health";

    /// <summary>Installs the WebSocket middleware and maps health, the Responses endpoint, and the call socket.</summary>
    /// <param name="app">The application to map on.</param>
    /// <param name="responsesPattern">
    /// The route the OpenAI-compatible Responses endpoint answers on, or <see langword="null"/> for
    /// <see cref="ResponsesEndpointRouteBuilderExtensions.DefaultPattern"/>.
    /// </param>
    /// <param name="responsesEntry">The entry key the Responses route answers on.</param>
    /// <param name="callEntry">The entry key the call route answers on.</param>
    /// <returns>The same application, so a host chains its calls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static WebApplication MapAgentCoreHost(
        this WebApplication app, string? responsesPattern = null, string responsesEntry = "main", string callEntry = "main")
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(responsesEntry);
        ArgumentException.ThrowIfNullOrEmpty(callEntry);

        app.MapGet(HealthPattern, () => Results.Ok("ok"));

        app.UseWebSockets();

        app.MapResponses(responsesPattern ?? ResponsesEndpointRouteBuilderExtensions.DefaultPattern, responsesEntry);

        app.MapCall(callEntry);

        return app;
    }
}

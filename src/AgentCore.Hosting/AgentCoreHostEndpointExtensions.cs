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

    /// <summary>Installs the WebSocket middleware and maps health, chat completions, and the call socket.</summary>
    /// <param name="app">The application to map on.</param>
    /// <param name="chatCompletionsPattern">
    /// The route the OpenAI-compatible text endpoint answers on, or <see langword="null"/> for
    /// <see cref="ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern"/>. A host that mounts
    /// another OpenAI-compatible surface of its own moves this one out of the way here.
    /// </param>
    /// <returns>The same application, so a host chains its calls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static WebApplication MapAgentCoreHost(this WebApplication app, string? chatCompletionsPattern = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(HealthPattern, () => Results.Ok("ok"));

        app.UseWebSockets();

        app.MapChatCompletions(chatCompletionsPattern ?? ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern);

        app.MapCall();

        return app;
    }
}

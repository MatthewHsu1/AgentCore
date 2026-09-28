using AgentCore.AspNetCore.Voice;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentCore.Hosting
{
    /// <summary>
    /// Maps every route AgentCore answers on, in one conversation.
    /// </summary>
    public static class AgentCoreHostEndpointExtensions
    {
        /// <summary>The route the liveness check answers on.</summary>
        public const string HealthPattern = "/health";

        /// <summary>Installs the WebSocket middleware and maps health, the Responses endpoint, and the conversation socket.</summary>
        /// <param name="app">The application to map on.</param>
        /// <param name="responsesPattern">
        /// The route the OpenAI-compatible Responses endpoint answers on, with <c>{entry}</c> naming
        /// the entry unless the host attaches an entry selector, or <see langword="null"/> for
        /// <see cref="ResponsesEndpointRouteBuilderExtensions.DefaultPattern"/>.
        /// </param>
        /// <param name="conversationPattern">
        /// The route the conversation socket answers on, with <c>{entry}</c> naming the entry unless the
        /// host attaches an entry selector, or
        /// <see langword="null"/> for <see cref="ConversationEndpointRouteBuilderExtensions.DefaultPattern"/>.
        /// </param>
        /// <returns>The mapped routes, so a host adds its own conventions to each.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
        public static AgentCoreEndpoints MapAgentCoreHost(
            this WebApplication app, string? responsesPattern = null, string? conversationPattern = null)
        {
            ArgumentNullException.ThrowIfNull(app);

            IEndpointConventionBuilder health = app.MapGet(HealthPattern, () => Results.Ok("ok"));

            _ = app.UseWebSockets();

            return new AgentCoreEndpoints(
                health,
                app.MapResponses(responsesPattern ?? ResponsesEndpointRouteBuilderExtensions.DefaultPattern),
                app.MapCall(conversationPattern ?? ConversationEndpointRouteBuilderExtensions.DefaultPattern));
        }
    }
}

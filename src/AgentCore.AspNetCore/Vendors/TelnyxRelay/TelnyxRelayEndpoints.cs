using System.Net.WebSockets;
using AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay
{
    /// <summary>
    /// Maps the Telnyx Conversation Relay socket onto the turn loop.
    /// </summary>
    internal static class TelnyxRelayEndpointRouteBuilderExtensions
    {
        /// <summary>The route the test host maps this endpoint on.</summary>
        public const string DefaultPattern = "/v1/{" + AgentCore.AspNetCore.Voice.Routing.ConversationEndpointRouteBuilderExtensions.EntryRouteParameter + "}/telnyx/relay";

        /// <summary>Maps the socket on one route, with the limits the host chose.</summary>
        /// <param name="endpoints">The route builder of the host.</param>
        /// <param name="pattern">The route to answer on. It must carry the <c>{entry}</c> parameter.</param>
        /// <param name="options">What the endpoint may do, and for how long.</param>
        /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
        public static IEndpointConventionBuilder MapTelnyxRelay(
            this IEndpointRouteBuilder endpoints,
            string pattern,
            TelnyxRelayOptions options)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrEmpty(pattern);
            ArgumentNullException.ThrowIfNull(options);

            // Map, and not MapGet. An HTTP/2 WebSocket arrives as CONNECT rather than GET, and MapGet
            // would answer 405 to it.
            return endpoints.Map(pattern, http => HandleAsync(http, options));
        }

        internal static async Task HandleAsync(HttpContext http, TelnyxRelayOptions options)
        {
            if (http.Features.Get<IHttpWebSocketFeature>() is null)
            {
                throw new InvalidOperationException(
                    "the relay endpoint needs the WebSocket middleware. Call app.UseWebSockets() before "
                    + "app.MapCall().");
            }

            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

            // The pipeline must stay on the stack for the whole life of the socket. A handler that
            // returns early gets "Cannot write to the response body, the response has completed".
            await TelnyxRelayConnection.RunAsync(http, socket, options).ConfigureAwait(false);
        }
    }
}

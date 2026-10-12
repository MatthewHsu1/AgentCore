using System.Collections.Frozen;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>Asks the host's hooks whether the caller may touch the conversation a request names.</summary>
    internal static class AgentCoreConversationGate
    {
        /// <summary>The code a request gets for a conversation that does not exist or that the caller may not touch.</summary>
        public const string NotFoundCode = "thread_not_found";

        /// <summary>Runs <c>BeforeConversation</c> for one request.</summary>
        /// <param name="http">A request routing matched to an AgentCore endpoint.</param>
        /// <param name="entry">The entry the request runs.</param>
        /// <param name="conversationId">The conversation the request names.</param>
        /// <param name="resuming">Whether the request answers an approval.</param>
        /// <param name="cancellationToken">Cancels the gate.</param>
        /// <returns><see langword="true"/> when the request may go on.</returns>
        public static ValueTask<bool> AllowAsync(
            HttpContext http, string entry, string conversationId, bool resuming, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(http);

            Endpoint? endpoint = http.GetEndpoint();
            
            ConversationOffer offer = new(
                http.User.Identity?.IsAuthenticated == true ? http.User : null,
                http.Request.Headers.ToFrozenDictionary(static header => header.Key, static header => string.Join(',', header.Value.ToArray()), StringComparer.OrdinalIgnoreCase),
                (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint?.DisplayName ?? string.Empty,
                entry,
                conversationId,
                endpoint?.Metadata.GetMetadata<AgentCoreRouteMetadata>()?.Transport ?? EntryTransport.Http,
                resuming);

            return ConversationGateChain.AllowAsync(http.RequestServices.GetRequiredService<AgentCoreBoot>().Hooks, offer, cancellationToken);
        }
    }
}

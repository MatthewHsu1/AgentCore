using System.Collections.Frozen;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>Learns the entry a request on an AgentCore route runs.</summary>
    public static class AgentCoreEntries
    {
        /// <summary>
        /// Runs the entry gate once per request and keeps the answer: a hook's choice, else the route's
        /// <c>{entry}</c> value.
        /// </summary>
        /// <param name="http">A request that routing has already matched to an endpoint.</param>
        /// <param name="cancellationToken">Cancels the gate.</param>
        /// <returns>The entry key, or <see langword="null"/> when a hook refused or the route names no entry.</returns>
        /// <exception cref="InvalidOperationException">Routing has not matched the request to an endpoint yet.</exception>
        public static async ValueTask<string?> ResolveAsync(HttpContext http, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(http);

            if (http.Features.Get<EntryFeature>() is { } known)
            {
                return known.Entry;
            }

            Endpoint endpoint = http.GetEndpoint()
                ?? throw new InvalidOperationException(
                    "the request has no endpoint yet, so no route names its entry. Resolve the entry after app.UseRouting().");

            HookRuntime hooks = http.RequestServices.GetRequiredService<AgentCoreBoot>().Hooks;
            string? requested = http.Request.RouteValues[ResponsesEndpointRouteBuilderExtensions.EntryRouteParameter] as string;

            string? entry = requested;
            if (hooks.Gates.Overrides(GatePoint.BeforeEntry))
            {
                entry = await EntryGateChain.ChooseAsync(hooks, OfferOf(http, endpoint, requested), cancellationToken).ConfigureAwait(false);
            }

            http.Features.Set(new EntryFeature(entry));
            return entry;
        }

        /// <summary>Reads the entry this request already resolved, or <see langword="null"/>.</summary>
        /// <param name="http">The request.</param>
        /// <returns>The entry key, or <see langword="null"/> when the gate has not run or refused.</returns>
        internal static string? Known(HttpContext http)
        {
            return http.Features.Get<EntryFeature>()?.Entry;
        }

        private static EntryOffer OfferOf(HttpContext http, Endpoint endpoint, string? requested)
        {
            return new EntryOffer(
                http.User.Identity?.IsAuthenticated == true ? http.User : null,
                http.Request.Headers.ToFrozenDictionary(static header => header.Key, static header => string.Join(',', header.Value.ToArray()), StringComparer.OrdinalIgnoreCase),
                (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName ?? string.Empty,
                requested,
                endpoint.Metadata.GetMetadata<AgentCoreRouteMetadata>()?.Transport ?? EntryTransport.Http);
        }

        private sealed record EntryFeature(string? Entry);
    }
}

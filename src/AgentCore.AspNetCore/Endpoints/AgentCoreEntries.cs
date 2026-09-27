using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>Learns the entry a request on an AgentCore route runs.</summary>
    public static class AgentCoreEntries
    {
        /// <summary>
        /// Runs the endpoint's selector, or reads <c>{entry}</c> when it has none, once per request, and keeps the
        /// answer as <see cref="IEntryFeature"/>.
        /// </summary>
        /// <param name="http">A request that routing has already matched to an endpoint.</param>
        /// <param name="cancellationToken">Cancels the selector.</param>
        /// <returns>
        /// The entry key, or <see langword="null"/> when the selector refused. A route with no selector and no
        /// <c>{entry}</c> value answers the empty string, which no entry declares.
        /// </returns>
        /// <exception cref="InvalidOperationException">Routing has not matched the request to an endpoint yet.</exception>
        public static async ValueTask<string?> ResolveAsync(HttpContext http, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(http);

            if (http.Features.Get<IEntryFeature>() is { } known)
            {
                return known.Entry;
            }

            if (http.Features.Get<EntryRefused>() is not null)
            {
                return null;
            }

            Endpoint endpoint = http.GetEndpoint()
                ?? throw new InvalidOperationException(
                    "the request has no endpoint yet, so no route names its entry. Resolve the entry after "
                    + "app.UseRouting().");

            string? entry = endpoint.Metadata.GetMetadata<EntrySelectorMetadata>() is { } metadata
                ? await ((IEntrySelector)http.RequestServices.GetRequiredService(metadata.SelectorType))
                    .SelectAsync(http, cancellationToken)
                    .ConfigureAwait(false)
                : http.Request.RouteValues[ResponsesEndpointRouteBuilderExtensions.EntryRouteParameter] as string ?? string.Empty;

            if (entry is null)
            {
                http.Features.Set(EntryRefused.Instance);
            }
            else
            {
                http.Features.Set<IEntryFeature>(new EntryFeature(entry));
            }

            return entry;
        }

        private sealed class EntryFeature(string entry) : IEntryFeature
        {
            public string Entry { get; } = entry;
        }

        private sealed class EntryRefused
        {
            public static EntryRefused Instance { get; } = new();
        }
    }
}

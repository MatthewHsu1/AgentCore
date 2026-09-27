using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>
    /// Maps the inbound conversation route onto whichever transport the document names.
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
        {
            return endpoints.MapCall(DefaultPattern);
        }

        /// <summary>Maps every entry on one route.</summary>
        /// <param name="endpoints">The route builder of the host.</param>
        /// <param name="pattern">
        /// The route to answer on. It carries the <c>{entry}</c> parameter, or the host attaches an
        /// <see cref="IEntrySelector"/> with <see cref="EntrySelectorEndpointConventionBuilderExtensions.SelectEntry{TSelector}"/>.
        /// Startup fails when it has neither.
        /// </param>
        /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
        public static IEndpointConventionBuilder MapCall(this IEndpointRouteBuilder endpoints, string pattern)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrEmpty(pattern);

            // Map, and not MapGet. An HTTP/2 WebSocket arrives as CONNECT rather than GET, and MapGet
            // would answer 405 to it.
            return endpoints.Map(pattern, http => DispatchAsync(http, pattern)).WithMetadata(AgentCoreRouteMetadata.Instance);
        }

        /// <summary>Reads the entry this request runs.</summary>
        /// <param name="http">The request on a conversation route.</param>
        /// <returns>
        /// The entry <see cref="AgentCoreEntries.ResolveAsync"/> kept, else the <c>{entry}</c> route value, else the
        /// empty string.
        /// </returns>
        public static string EntryOf(HttpContext http)
        {
            ArgumentNullException.ThrowIfNull(http);
            return http.Features.Get<IEntryFeature>()?.Entry
                ?? http.Request.RouteValues[EntryRouteParameter] as string
                ?? string.Empty;
        }

        private static async Task DispatchAsync(HttpContext http, string pattern)
        {
            if (http.RequestServices.GetService<AgentCoreBoot>() is not { } boot)
            {
                await NotRoutedAsync(http, pattern, "this host registered no AgentCore services").ConfigureAwait(false);
                return;
            }

            if (boot.ConversationHandler is not { } handler)
            {
                await NotRoutedAsync(http, pattern, boot.ConversationUnroutable ?? "this host routes no inbound conversation")
                    .ConfigureAwait(false);
                return;
            }

            // Before the handler, so a refused caller never gets a socket upgrade.
            if (await AgentCoreEntries.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false) is not { } entry)
            {
                await WriteProblemAsync(
                    http, StatusCodes.Status403Forbidden, "This route runs no entry for this caller.",
                    "entry_refused: the route's entry selector refused this caller.", http.Request.Path)
                    .ConfigureAwait(false);
                return;
            }

            await (boot.Entries.Entries.Contains(entry, StringComparer.Ordinal)
                ? handler(http)
                : UnknownEntryAsync(http, EntryRegistry.UnknownEntryMessage(entry, boot.Entries.Entries))).ConfigureAwait(false);
        }

        private static async Task NotRoutedAsync(HttpContext http, string pattern, string reason)
        {
            ILogger logger = (http.RequestServices.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
                .CreateLogger(typeof(ConversationEndpointRouteBuilderExtensions).FullName!);

            ConversationRouteLog.RouteNotMapped(logger, pattern, reason);

            await WriteProblemAsync(http, StatusCodes.Status503ServiceUnavailable, "This host routes no inbound conversation.", reason, pattern)
                .ConfigureAwait(false);
        }

        private static Task UnknownEntryAsync(HttpContext http, string reason)
        {
            return WriteProblemAsync(http, StatusCodes.Status404NotFound, "No entry answers on this route.", reason, http.Request.Path);
        }

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
}

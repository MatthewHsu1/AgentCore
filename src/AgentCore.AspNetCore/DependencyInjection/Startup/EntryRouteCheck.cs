using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>
    /// Fails startup when an AgentCore route has no way to learn its entry, or names a selector the host never
    /// registered.
    /// </summary>
    internal sealed class EntryRouteCheck : IStartupFilter
    {
        /// <inheritdoc/>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                next(app);
                Check(app.ApplicationServices);
            };
        }

        /// <summary>Walks every mapped route AgentCore tagged.</summary>
        /// <param name="services">The root provider.</param>
        /// <exception cref="ConfigurationLoadException">A route names no entry, or its selector is not registered.</exception>
        internal static void Check(IServiceProvider services)
        {
            if (services.GetService<EndpointDataSource>() is not { } routes)
            {
                return;
            }

            IServiceProviderIsService registered = services.GetRequiredService<IServiceProviderIsService>();

            foreach (RouteEndpoint route in routes.Endpoints.OfType<RouteEndpoint>())
            {
                if (route.Metadata.GetMetadata<AgentCoreRouteMetadata>() is null)
                {
                    continue;
                }

                string pattern = route.RoutePattern.RawText ?? route.DisplayName ?? "(unnamed route)";

                if (route.Metadata.GetMetadata<EntrySelectorMetadata>() is { } selector)
                {
                    if (!registered.IsService(selector.SelectorType))
                    {
                        throw new ConfigurationLoadException(
                            $"The route '{pattern}' picks its entry with {selector.SelectorType.FullName}, which the "
                            + "host never registered. Add it to the service collection.");
                    }
                }
                else if (route.RoutePattern.GetParameter(ResponsesEndpointRouteBuilderExtensions.EntryRouteParameter) is null)
                {
                    throw new ConfigurationLoadException(
                        $"The route '{pattern}' carries no {{{ResponsesEndpointRouteBuilderExtensions.EntryRouteParameter}}} "
                        + "parameter and no entry selector, so nothing names its entry. Add {entry} to the pattern, "
                        + "or call SelectEntry on the route.");
                }
            }
        }
    }
}

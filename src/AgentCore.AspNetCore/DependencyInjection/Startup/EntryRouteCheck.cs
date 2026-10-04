using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>Fails startup when an AgentCore route has no way to learn its entry.</summary>
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
        /// <exception cref="ConfigurationLoadException">A route names no entry and no hook overrides the entry gate.</exception>
        internal static void Check(IServiceProvider services)
        {
            if (services.GetService<EndpointDataSource>() is not { } routes)
            {
                return;
            }

            bool gated = services.GetRequiredService<IOptions<AgentCoreOptions>>().Value.HookFactories
                .Any(static entry => HookTable.TypeOverrides(entry.HookType, GatePoint.BeforeEntry));

            foreach (RouteEndpoint route in routes.Endpoints.OfType<RouteEndpoint>())
            {
                if (route.Metadata.GetMetadata<AgentCoreRouteMetadata>() is null
                    || gated
                    || route.RoutePattern.GetParameter(ResponsesEndpointRouteBuilderExtensions.EntryRouteParameter) is not null)
                {
                    continue;
                }

                string pattern = route.RoutePattern.RawText ?? route.DisplayName ?? "(unnamed route)";
                throw new ConfigurationLoadException(
                    $"The route '{pattern}' carries no {{{ResponsesEndpointRouteBuilderExtensions.EntryRouteParameter}}} "
                    + $"parameter and no hook overrides {nameof(AgentHook.BeforeEntryAsync)}, so nothing names its entry. "
                    + "Add {entry} to the pattern, or register a hook with options.UseHooks(...) that chooses the entry.");
            }
        }
    }
}

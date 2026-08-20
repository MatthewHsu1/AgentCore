using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// Maps the one inbound call route, onto whichever transport the document names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route string is the same whichever vendor answers it.</b> That is the whole point: a
/// host names a path once, and changing vendors is a document edit. Before this existed, every
/// host had to name its vendor in code.
/// </para>
/// <para>
/// The host owns the WebSocket middleware, and its defaults suit a browser rather than a call.
/// Call <c>app.UseWebSockets()</c> before this, and use <c>AddAgentCoreWebSockets()</c> for the
/// twenty-second keep-alive numbers a call needs. The shipped default is two minutes with no
/// timeout, which lets a dead call hold a session for two minutes.
/// </para>
/// </remarks>
public static class CallEndpointRouteBuilderExtensions
{
    /// <summary>The route the call transport answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/call";

    /// <summary>Maps the inbound call route on <see cref="DefaultPattern"/>.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <returns>The mapped endpoint, or a builder over nothing when no transport was selected.</returns>
    public static IEndpointConventionBuilder MapCall(this IEndpointRouteBuilder endpoints)
        => endpoints.MapCall(DefaultPattern);

    /// <summary>Maps the inbound call route on one route.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <returns>
    /// The mapped endpoint, so a host adds its own conventions — or a builder over nothing, when
    /// no transport was selected. A convention added to that one is added to no endpoint, which is
    /// what "nothing is dialled in to this host" means.
    /// </returns>
    /// <exception cref="Application.Configuration.Parsing.ConfigurationLoadException">
    /// <c>providers.call.kind</c> names a vendor no registered adapter serves, or one that two of
    /// them answer to.
    /// </exception>
    public static IEndpointConventionBuilder MapCall(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        var services = endpoints.ServiceProvider;
        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger(typeof(CallEndpointRouteBuilderExtensions).FullName!);

        // Every lookup is GetService and never GetRequiredService, on purpose. A host may map this
        // route with no AgentCore registration at all, and such a host must get a readable reason
        // rather than a resolution failure.
        if (services.GetService<IReadOnlyList<ICallAdapter>>() is not { } adapters)
        {
            CallRouteLog.RouteNotMapped(logger, pattern, "this host registered no call adapter");
            return UnmappedEndpoint.Instance;
        }

        if (services.GetService<AgentCoreConfiguration>()?.Providers?.Call is not { } entry)
        {
            CallRouteLog.RouteNotMapped(logger, pattern, "this host loaded no providers.call block");
            return UnmappedEndpoint.Instance;
        }

        var selected = VendorAdapterSelector.Select(entry.Kind, adapters, CallSeams.Call);

        if (selected is not ICallTransportAdapter transport)
        {
            // A vendor this process dials out to has no inbound URL. That is not a failure: it is
            // the other half of the seam working.
            CallRouteLog.DialOutVendorMapsNoRoute(logger, selected.Kind, pattern);
            return UnmappedEndpoint.Instance;
        }

        return transport.Map(endpoints, pattern, entry);
    }
}

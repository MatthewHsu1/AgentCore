using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// A call vendor this process is dialled <b>in</b> to, which therefore owns an inbound route.
/// </summary>
/// <remarks>
/// The routing face of <see cref="ICallAdapter"/>. It lives here and not in
/// <c>AgentCore.Application</c> because it names <see cref="IEndpointRouteBuilder"/>, and the core
/// references no ASP.NET Core package.
/// </remarks>
public interface ICallTransportAdapter : ICallAdapter
{
    /// <summary>Maps this vendor's socket onto one route.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <param name="configuration">The <c>providers.call</c> block, including its limits.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    IEndpointConventionBuilder Map(
        IEndpointRouteBuilder endpoints,
        string pattern,
        CallProviderConfiguration configuration);
}

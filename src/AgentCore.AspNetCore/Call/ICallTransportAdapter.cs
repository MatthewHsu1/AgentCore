using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// A call vendor this process is dialled <b>in</b> to, which therefore owns an inbound route.
/// </summary>
public interface ICallTransportAdapter : ICallAdapter
{
    /// <summary>Builds the handler the call route answers every entry with.</summary>
    /// <param name="configuration">The <c>providers.call</c> block, including its limits.</param>
    /// <returns>
    /// The delegate the call route runs. The URL names the entry, and the route has already
    /// refused one the document does not declare; the handler reads it with
    /// <see cref="CallEndpointRouteBuilderExtensions.EntryOf"/>.
    /// </returns>
    RequestDelegate CreateHandler(CallProviderConfiguration configuration);
}

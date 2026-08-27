using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// A call vendor this process is dialled <b>in</b> to, which therefore owns an inbound route.
/// </summary>
public interface ICallTransportAdapter : ICallAdapter
{
    /// <summary>Builds the handler this vendor answers a call with.</summary>
    /// <param name="configuration">The <c>providers.call</c> block, including its limits.</param>
    /// <returns>The delegate the call route runs.</returns>
    RequestDelegate CreateHandler(CallProviderConfiguration configuration);
}

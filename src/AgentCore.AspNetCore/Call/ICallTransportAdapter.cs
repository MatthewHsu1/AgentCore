using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// A call vendor this process is dialled <b>in</b> to, which therefore owns an inbound route.
/// </summary>
public interface ICallTransportAdapter : ICallAdapter
{
    /// <summary>Builds the handler one entry answers a call with.</summary>
    /// <param name="configuration">The <c>providers.call</c> block, including its limits.</param>
    /// <param name="entryName">The entry key the handler answers on.</param>
    /// <returns>The delegate the entry's call route runs.</returns>
    RequestDelegate CreateHandler(CallProviderConfiguration configuration, string entryName);
}

using Microsoft.Extensions.Hosting;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>A started host, read as the container it is.</summary>
/// <param name="host">The host to read services from, and to close on the way out.</param>
/// <remarks>
/// Disposing this disposes the host, which disposes the container. That is the whole shutdown
/// path: nothing here calls StopAsync, because a host that failed to start never gets one.
/// </remarks>
internal sealed class StartedHost(IHost host) : IServiceProvider, IDisposable
{
    public object? GetService(Type serviceType) => host.Services.GetService(serviceType);

    public void Dispose() => host.Dispose();
}

using AgentCore.Application.Ports;
using Microsoft.Extensions.Hosting;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Drains the telemetry session on the way out, so a stopping process exports its last batch.</summary>
internal sealed class TelemetrySessionOwner(ITelemetrySession session) : IHostedService, IAsyncDisposable
{
    private int _flushed;

    /// <summary>Starts nothing: the composition root already started the session.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Drains the session, inside the host's ordered shutdown.</summary>
    /// <param name="cancellationToken">Unused: a cancelled flush is a dropped batch.</param>
    /// <returns>A task that completes when the last batch has been exported.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => FlushAsync().AsTask();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => FlushAsync();

    private ValueTask FlushAsync()
        => Interlocked.Exchange(ref _flushed, 1) == 0 ? session.DisposeAsync() : ValueTask.CompletedTask;
}

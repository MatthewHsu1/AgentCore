using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Closes what a startup step opened, in the order it was given, when the host stops.</summary>
internal sealed class StartupResourceOwner : IHostedService, IAsyncDisposable, IDisposable
{
    private readonly object[] _resources;

    private int _closed;

    /// <summary>Takes ownership of one startup step's resources.</summary>
    /// <param name="resources">
    /// What to close, in the order to close it. The order is load-bearing wherever one resource
    /// writes into another: the writer is closed first, so its target is still open to receive.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="resources"/> is <see langword="null"/>.</exception>
    public StartupResourceOwner(params object[] resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _resources = resources;
    }

    /// <summary>Puts one startup step's resources under an owner of their own.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="resources">What to close, in the order to close it.</param>
    /// <remarks>
    /// Not <c>AddHostedService</c>: that goes through <c>TryAddEnumerable</c>, which deduplicates by
    /// implementation type, so the second owner of this type would be dropped without a word and the
    /// resources behind it never closed.
    /// </remarks>
    public static void Own(IServiceCollection services, params object[] resources)
        => services.AddSingleton<IHostedService>(_ => new StartupResourceOwner(resources));

    /// <summary>Starts nothing: the composition root already opened every resource.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Closes every resource, inside the host's ordered shutdown.</summary>
    /// <param name="cancellationToken">Unused: a cancelled close is a lost write.</param>
    /// <returns>A task that completes when the last resource is closed.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => CloseAsync().AsTask();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => CloseAsync();

    /// <summary>Closes every resource, blocking until it is done.</summary>
    /// <remarks>
    /// A container disposed synchronously reaches this, and it must not be the path that loses
    /// writes. There is no synchronization context to deadlock against in a host.
    /// </remarks>
    public void Dispose() => CloseAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Closes each resource once, in order, whichever disposal interface it carries.</summary>
    /// <returns>A task that completes when the last resource is closed.</returns>
    private async ValueTask CloseAsync()
    {
        // Stop and the dispose that follows both reach here, and a resource is not required to
        // survive being closed twice.
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        foreach (object resource in _resources)
        {
            switch (resource)
            {
                // Asynchronous first: a resource that carries both, as the audit queue does, must
                // not be drained on the path that blocks a thread while it waits.
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;

                case IDisposable disposable:
                    disposable.Dispose();
                    break;

                default:
                    break;
            }
        }
    }
}

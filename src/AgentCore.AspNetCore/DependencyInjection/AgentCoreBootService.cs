using Microsoft.Extensions.Hosting;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Runs the boot, before anything a half-booted graph could answer.</summary>
/// <param name="boot">The owner every opened resource belongs to.</param>
internal sealed class AgentCoreBootService(AgentCoreBoot boot) : IHostedLifecycleService
{
    /// <summary>Loads the document and opens everything it names.</summary>
    /// <param name="cancellationToken">Cancels the secret reads and the adapter builds.</param>
    /// <returns>A task that completes when the graph is ready to take a call.</returns>
    public Task StartingAsync(CancellationToken cancellationToken)
        => boot.BootAsync(cancellationToken).AsTask();

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing closes here. A failed start never calls <c>StopAsync</c> at all, so shutdown lives in
    /// <see cref="AgentCoreBoot.DisposeAsync"/>, which the container reaches on both paths.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

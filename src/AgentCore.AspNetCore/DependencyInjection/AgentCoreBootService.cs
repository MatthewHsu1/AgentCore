using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using Microsoft.Extensions.Hosting;

namespace AgentCore.AspNetCore.DependencyInjection
{
    /// <summary>Runs the boot, before anything a half-booted graph could answer.</summary>
    /// <param name="boot">The owner every opened resource belongs to.</param>
    internal sealed class AgentCoreBootService(AgentCoreBoot boot) : IHostedLifecycleService
    {
        /// <summary>Loads the document and opens everything it names.</summary>
        /// <param name="cancellationToken">Cancels the secret reads and the adapter builds.</param>
        /// <returns>A task that completes when the graph is ready to take a conversation.</returns>
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            return boot.BootAsync(cancellationToken).AsTask();
        }

        /// <inheritdoc/>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task StartedAsync(CancellationToken cancellationToken)
        {
            if (boot.TryGetHooks(out HookRuntime? hooks))
            {
                _ = HookStartup.RaiseHostNotice(
                    hooks, scope => new HostStarted(scope, [.. boot.Entries.Entries], boot.Tools.Ids.Count));
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task StoppingAsync(CancellationToken cancellationToken)
        {
            if (boot.TryGetHooks(out HookRuntime? hooks))
            {
                _ = HookStartup.RaiseHostNotice(hooks, scope => new HostStopping(scope));
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

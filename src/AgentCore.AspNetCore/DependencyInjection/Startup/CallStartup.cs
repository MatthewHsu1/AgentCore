using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Step 4c: open the store 0 backing the document names, before the document is compiled.</summary>
internal static class CallStartup
{
    /// <summary>Opens the store <c>providers.calls</c> names, or the built-in one.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.calls</c>.</param>
    /// <param name="options">The options the host filled. It carries the call store vendors.</param>
    /// <param name="loggers">The factory the defaulting warning is written through.</param>
    /// <param name="cancellationToken">Cancels the store open.</param>
    /// <returns>The store, which is never <see langword="null"/>.</returns>
    internal static async ValueTask<ICallStore> OpenAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ICallStore store = await CallStoreFactory
            .OpenAsync(
                configuration,
                options.SecretResolver,
                options.CallStores ?? [],
                cancellationToken)
            .ConfigureAwait(false);

        if (store is InMemoryCallStore && configuration.Providers?.Calls is null)
        {
            StartupLog.CallStoreDefaulted(loggers.CreateLogger<InMemoryCallStore>());
        }

        return store;
    }
}

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Application.Transcript.Memory;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Step 4b: open the store 1 backing the document names, before the document is compiled.</summary>
internal static class TranscriptStartup
{
    /// <summary>Opens the store <c>providers.transcript</c> names, or the built-in one.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.transcript</c>.</param>
    /// <param name="options">The options the host filled. It carries the transcript vendors.</param>
    /// <param name="loggers">The factory the defaulting warning is written through.</param>
    /// <param name="cancellationToken">Cancels the store open.</param>
    /// <returns>The store, which is never <see langword="null"/>.</returns>
    internal static async ValueTask<ITranscriptStore> OpenAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ITranscriptStore store = await TranscriptStoreFactory
            .OpenAsync(
                configuration,
                options.SecretResolver,
                options.TranscriptStores ?? [],
                cancellationToken)
            .ConfigureAwait(false);

        if (store is InMemoryTranscriptStore && configuration.Providers?.Transcript is null)
        {
            StartupLog.TranscriptStoreDefaulted(loggers.CreateLogger<InMemoryTranscriptStore>());
        }

        return store;
    }
}

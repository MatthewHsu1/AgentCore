using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Step 2b: start the telemetry export, before anything below writes a line worth keeping.</summary>
internal static class TelemetryStartup
{
    /// <summary>Starts the export the document names, and registers it for shutdown.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The loaded document. It carries <c>providers.telemetry</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors.</param>
    /// <param name="loggers">The factory every seam below takes its loggers from.</param>
    /// <param name="cancellationToken">Cancels the adapter build.</param>
    /// <remarks>
    /// <para>
    /// <c>providers.telemetry.kind</c> picks one of the vendors the host registered, and a document
    /// that names none exports nothing.
    /// </para>
    /// <para>
    /// The session is ADDED to the host's factory rather than replacing anything: a deployment
    /// reading its console keeps reading its console, and the same line also reaches the collector.
    /// <c>AddProvider</c> refreshes the loggers the factory already handed out, so a category created
    /// before this runs is exported too.
    /// </para>
    /// </remarks>
    internal static async ValueTask StartAsync(
        IServiceCollection services,
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ITelemetrySession? telemetry = options.Telemetry is { } telemetryAdapters
            ? await TelemetrySessionFactory
                .StartAsync(configuration, options.SecretResolver, telemetryAdapters, cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (telemetry?.Logs is { } telemetryLogs)
        {
            loggers.AddProvider(telemetryLogs);
        }

        if (telemetry is not null)
        {
            // The container owns the shutdown. Disposal is what flushes the batch a stopping process
            // is still holding, so a session nobody disposed would drop it.
            services.AddSingleton(telemetry);
        }
    }
}

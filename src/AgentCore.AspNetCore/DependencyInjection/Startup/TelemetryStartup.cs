using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Step 2b: start the telemetry export, before anything below writes a line worth keeping.</summary>
internal static class TelemetryStartup
{
    /// <summary>Starts the export the document names, and attaches its log provider.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.telemetry</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors.</param>
    /// <param name="loggers">The factory every seam below takes its loggers from.</param>
    /// <param name="cancellationToken">Cancels the adapter build.</param>
    /// <returns>The session, or <see langword="null"/> when the host registered no vendor.</returns>
    internal static async ValueTask<ITelemetrySession?> StartAsync(
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

        return telemetry;
    }
}

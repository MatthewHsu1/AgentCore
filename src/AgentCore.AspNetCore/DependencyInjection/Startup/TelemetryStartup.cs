using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Step 2b: start the telemetry export, before anything below writes a line worth keeping.</summary>
internal static class TelemetryStartup
{
    /// <summary>Starts the export the document names, and registers it for shutdown.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The loaded document. It carries <c>providers.telemetry</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors.</param>
    /// <param name="loggers">The factory every seam below takes its loggers from.</param>
    /// <param name="cancellationToken">Cancels the adapter build.</param>
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
            services.AddSingleton(telemetry);

            // The registration above never flushes on its own. TelemetrySessionOwner explains why.
            services.AddHostedService(_ => new TelemetrySessionOwner(telemetry));
        }
    }
}

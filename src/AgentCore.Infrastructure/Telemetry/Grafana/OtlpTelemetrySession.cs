using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AgentCore.Infrastructure.Telemetry.Grafana;

/// <summary>
/// The three OpenTelemetry signals of one export, held together and shut down together.
/// </summary>
/// <remarks>
/// <para>
/// Traces and metrics are built standalone, through <see cref="Sdk"/>, rather than registered into a
/// service collection. That is what keeps every dependency-injection type out of
/// <c>AgentCore.Application</c>: the port hands back this object, and the object holds the providers.
/// </para>
/// <para>
/// <b>Logs take a different route on purpose.</b> The standalone log builder,
/// <c>Sdk.CreateLoggerProviderBuilder</c>, is marked experimental under <c>OTEL1000</c>, and this
/// repository builds warnings as errors. A private <see cref="ILoggerFactory"/> carrying the
/// OpenTelemetry provider does the same job through the stable surface, and
/// <see cref="ILoggerProvider.CreateLogger"/> forwards to it. Disposing that factory disposes the
/// exporter beneath it, which is the flush.
/// </para>
/// <para>
/// <b>Disposal is the flush.</b> Each provider batches onto a background thread, so a process that
/// exited without disposing would drop whatever had not been sent. Each signal gets a bounded moment
/// to drain, because a host that is stopping has a shutdown budget and a collector that has gone away
/// must not hold it.
/// </para>
/// </remarks>
/// <param name="tracer">The span provider.</param>
/// <param name="meter">The measurement provider.</param>
/// <param name="logs">The factory carrying the OpenTelemetry log provider.</param>
internal sealed class OtlpTelemetrySession(
    TracerProvider tracer,
    MeterProvider meter,
    ILoggerFactory logs) : ITelemetrySession, ILoggerProvider
{
    /// <summary>How long each signal is given to drain on the way out, in milliseconds.</summary>
    private const int FlushTimeoutMilliseconds = 5_000;

    /// <inheritdoc />
    public ILoggerProvider Logs => this;

    /// <inheritdoc />
    /// <remarks>
    /// The host's own factory calls this once for each category and holds what it gets, so the
    /// forwarded logger lives as long as the category does and nothing is created for each line.
    /// </remarks>
    public ILogger CreateLogger(string categoryName) => logs.CreateLogger(categoryName);

    /// <summary>Does nothing, because the host's logger factory owns no part of this session.</summary>
    /// <remarks>
    /// <see cref="ILoggerFactory"/> disposes the providers it was given. This session is handed to one
    /// as a provider and is also owned by the composition root, so honouring that call would shut the
    /// export down whenever the host factory went away and leave <see cref="DisposeAsync"/> flushing
    /// something already gone. Disposal happens in one place, below.
    /// </remarks>
    void IDisposable.Dispose()
    {
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Logs first: the factory stops accepting lines before the exporters under the other two
        // signals go away, so nothing is written into a provider that has already shut down.
        logs.Dispose();

        meter.ForceFlush(FlushTimeoutMilliseconds);
        meter.Dispose();

        tracer.ForceFlush(FlushTimeoutMilliseconds);
        tracer.Dispose();

        return ValueTask.CompletedTask;
    }
}

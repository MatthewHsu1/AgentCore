using System.Text;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgentCore.Infrastructure.Telemetry.Grafana;

/// <summary>
/// Exports traces, metrics, and logs to Grafana Cloud over OTLP. <c>kind: grafana</c>.
/// </summary>
/// <remarks>
/// <para>
/// D26 sends every signal to Grafana Cloud, and this is the one file that knows it. The library above
/// writes to an <c>ActivitySource</c> and a <c>Meter</c> and knows nothing about where they go, so a
/// deployment that exports somewhere else writes a sibling of this file and changes one line of its
/// document.
/// </para>
/// <para>
/// <b>What makes this Grafana rather than plain OTLP</b> is one thing: the credential. Grafana Cloud
/// authenticates OTLP with HTTP basic, where the instance id is the user and the token is the
/// password. Everything else here is the vendor-neutral OTLP setup, which is why the class is named
/// for both.
/// </para>
/// <para>
/// <b>No instrumentation package is referenced, on purpose.</b> .NET 10 emits the ASP.NET Core and
/// <c>HttpClient</c> signals from the framework itself, so listening to them costs a name in
/// <c>providers.telemetry.sources</c> and <c>providers.telemetry.meters</c>. Referencing
/// <c>OpenTelemetry.Instrumentation.AspNetCore</c> here would pull a web dependency into the outbound
/// adapter project for signals the framework already produces.
/// </para>
/// <para>
/// <b>T61 is enforced twice, and this is the second place.</b> The library keeps no call id on a
/// metric attribute; this keeps the export interval at a minute or more, because Grafana Cloud bills
/// the greater of active series and data points a minute. The floor lives on
/// <see cref="TelemetryProviderConfiguration.ExportIntervalMilliseconds"/>, so a document cannot
/// write its way under it.
/// </para>
/// </remarks>
public sealed class GrafanaOtlpTelemetryAdapter : ITelemetryAdapter
{
    /// <summary>The <c>providers.telemetry.kind</c> value this adapter serves.</summary>
    public const string GrafanaKind = "grafana";

    /// <summary>The path traces are posted to, under the base endpoint.</summary>
    internal const string TracesPath = "/v1/traces";

    /// <summary>The path metrics are posted to, under the base endpoint.</summary>
    internal const string MetricsPath = "/v1/metrics";

    /// <summary>The path log records are posted to, under the base endpoint.</summary>
    internal const string LogsPath = "/v1/logs";

    /// <inheritdoc />
    public string Kind => GrafanaKind;

    /// <summary>Joins the base endpoint of the document to one signal's path.</summary>
    /// <param name="baseEndpoint">The <c>providers.telemetry.endpoint</c> value.</param>
    /// <param name="signalPath">One of the three signal paths, each starting with a slash.</param>
    /// <returns>The full URL this signal is posted to.</returns>
    /// <remarks>
    /// <para>
    /// <b>This has to be done by hand, and getting it wrong is silent.</b> The OpenTelemetry .NET
    /// exporter appends <c>/v1/traces</c> and its siblings for you only when the endpoint came from
    /// the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable. Assigning
    /// <c>OtlpExporterOptions.Endpoint</c> in code sets <c>AppendSignalPathToEndpoint</c> to false,
    /// on the reasoning that code that names an endpoint names the whole of it. So a document that
    /// writes the gateway root would otherwise post all three signals to the root and read nothing
    /// back but a rejection.
    /// </para>
    /// <para>
    /// A document that writes no endpoint takes the other path: nothing is assigned, the variable
    /// answers, and the SDK does the appending. That is why this runs only when the document wrote
    /// one.
    /// </para>
    /// </remarks>
    internal static string SignalEndpoint(string baseEndpoint, string signalPath)
    {
        var root = baseEndpoint.TrimEnd('/');

        // A document that already wrote the signal path is taken at its word rather than given a
        // second copy of it.
        return root.EndsWith(signalPath, StringComparison.OrdinalIgnoreCase) ? root : root + signalPath;
    }

    /// <inheritdoc />
    public async ValueTask<ITelemetrySession> StartAsync(
        TelemetryProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string? headers = await CredentialAsync(secrets, cancellationToken).ConfigureAwait(false);

        Action<OtlpExporterOptions> ConfigureExporter(string signalPath) => options =>
        {
            // The Grafana Cloud OTLP gateway speaks HTTP with protobuf bodies, and it does not
            // answer gRPC. The OpenTelemetry .NET default on this target framework IS gRPC, so
            // leaving this alone sends every signal at a protocol the gateway never replies to.
            options.Protocol = OtlpExportProtocol.HttpProtobuf;

            if (entry.Endpoint is { Length: > 0 } endpoint)
            {
                options.Endpoint = new Uri(SignalEndpoint(endpoint, signalPath));
            }

            // Only overwrite the headers when a Grafana credential resolved. Leaving them alone is
            // what lets the standard OTEL_EXPORTER_OTLP_HEADERS variable still serve a collector that
            // authenticates some other way, or none at all.
            if (headers is not null)
            {
                options.Headers = headers;
            }
        };

        // One resource for all three signals, so a reader in Grafana joins a log line to the span it
        // was written under without a second lookup.
        var resource = ResourceBuilder.CreateDefault().AddService(entry.ServiceName);

        var tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(AgentCoreTelemetry.ActivitySourceName);

        foreach (var source in entry.Sources)
        {
            tracerBuilder.AddSource(source);
        }

        var meterBuilder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(AgentCoreTelemetry.MeterName);

        foreach (var meter in entry.Meters)
        {
            meterBuilder.AddMeter(meter);
        }

        var tracer = tracerBuilder.AddOtlpExporter(ConfigureExporter(TracesPath)).Build()
            ?? throw new InvalidOperationException("the OpenTelemetry SDK built no tracer provider.");

        var configureMetrics = ConfigureExporter(MetricsPath);
        
        var meters = meterBuilder
            .AddOtlpExporter((exporter, reader) =>
            {
                configureMetrics(exporter);
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                    entry.ExportIntervalMilliseconds;
            })
            .Build()
            ?? throw new InvalidOperationException("the OpenTelemetry SDK built no meter provider.");

        var logs = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            // Without these two the exported record carries the message template and the arguments
            // separately, and no scope at all, so a reader sees "{Stage} refused {Tool}" beside a
            // call it cannot place.
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.SetResourceBuilder(resource);
            options.AddOtlpExporter(ConfigureExporter(LogsPath));
        }));

        return new OtlpTelemetrySession(tracer, meters, logs);
    }

    /// <summary>Reads the Grafana Cloud credential and renders it as one OTLP header.</summary>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The <c>Authorization</c> header, or <see langword="null"/> when this deployment names no
    /// Grafana instance and therefore exports with no credential.
    /// </returns>
    /// <exception cref="SecretResolutionException">
    /// An instance id resolved and the token beside it did not.
    /// </exception>
    /// <remarks>
    /// The instance id is the switch. A collector running beside this process needs neither half, and
    /// a deployment that names Grafana fails at startup rather than sending telemetry that Grafana
    /// answers 401 to and nobody notices.
    /// </remarks>
    private static async ValueTask<string?> CredentialAsync(
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken)
    {
        string? instanceId = await secrets
            .TryReadAsync(KnownSecrets.GrafanaCloudInstanceId, cancellationToken)
            .ConfigureAwait(false);

        if (instanceId is not { Length: > 0 })
        {
            return null;
        }

        string token = await secrets.RequireAsync(
            KnownSecrets.GrafanaCloudApiToken,
            "This deployment named a Grafana Cloud instance, and OTLP export there needs the token beside it.",
            cancellationToken).ConfigureAwait(false);

        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(instanceId + ":" + token));

        return "Authorization=Basic " + basic;
    }
}

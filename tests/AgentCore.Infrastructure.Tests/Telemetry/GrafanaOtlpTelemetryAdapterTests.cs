using AgentCore.Infrastructure.Telemetry.Grafana;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Telemetry;

/// <summary>
/// The two things about the Grafana Cloud OTLP gateway that fail silently when they are wrong.
/// </summary>
/// <remarks>
/// <para>
/// Grafana Cloud offers two ways in. The native path gives Loki, Tempo, and Mimir an endpoint and a
/// numeric user id each, so three credentials. The OTLP gateway takes all three signals at one URL
/// under one basic credential, where the instance id is the user and the access policy token is the
/// password, and fans them out on its side. This adapter uses the gateway, which is why
/// <c>KnownSecrets</c> holds one pair and not three.
/// </para>
/// <para>
/// The gateway then wants a signal path under that one URL, and the OpenTelemetry .NET exporter
/// stops appending one the moment the endpoint is assigned in code. It is a rejection at the
/// collector rather than an exception here, so nothing in the process reports it. These tests are
/// the report.
/// </para>
/// </remarks>
public sealed class GrafanaOtlpTelemetryAdapterTests
{
    private const string Gateway = "https://otlp-gateway-prod-us-east-0.grafana.net/otlp";

    [Fact]
    public void EachSignal_GetsItsOwnPathUnderTheGatewayRoot()
    {
        Assert.Equal(
            Gateway + "/v1/traces",
            GrafanaOtlpTelemetryAdapter.SignalEndpoint(Gateway, GrafanaOtlpTelemetryAdapter.TracesPath));

        Assert.Equal(
            Gateway + "/v1/metrics",
            GrafanaOtlpTelemetryAdapter.SignalEndpoint(Gateway, GrafanaOtlpTelemetryAdapter.MetricsPath));

        Assert.Equal(
            Gateway + "/v1/logs",
            GrafanaOtlpTelemetryAdapter.SignalEndpoint(Gateway, GrafanaOtlpTelemetryAdapter.LogsPath));
    }

    [Fact]
    public void ThreeSignals_NeverShareOneUrl()
    {
        // The failure this guards is one signal's data arriving at another's ingest path. The
        // gateway answers that with a rejection and the process carries on, so a shared URL would
        // look exactly like working telemetry from inside the host.
        var traces = GrafanaOtlpTelemetryAdapter.SignalEndpoint(Gateway, GrafanaOtlpTelemetryAdapter.TracesPath);
        var metrics = GrafanaOtlpTelemetryAdapter.SignalEndpoint(Gateway, GrafanaOtlpTelemetryAdapter.MetricsPath);
        var logs = GrafanaOtlpTelemetryAdapter.SignalEndpoint(Gateway, GrafanaOtlpTelemetryAdapter.LogsPath);

        Assert.Equal(3, new HashSet<string>([traces, metrics, logs], StringComparer.Ordinal).Count);
    }

    [Fact]
    public void ATrailingSlash_DoesNotDoubleUp()
    {
        // The console prints the endpoint without a trailing slash and a human pastes it with one.
        Assert.Equal(
            Gateway + "/v1/traces",
            GrafanaOtlpTelemetryAdapter.SignalEndpoint(Gateway + "/", GrafanaOtlpTelemetryAdapter.TracesPath));
    }

    [Fact]
    public void AnEndpointThatAlreadyNamesItsSignal_IsTakenAtItsWord()
    {
        // A deployment reading the OpenTelemetry docs rather than the Grafana ones writes the whole
        // path. Appending a second copy would post to /v1/traces/v1/traces.
        Assert.Equal(
            Gateway + "/v1/traces",
            GrafanaOtlpTelemetryAdapter.SignalEndpoint(Gateway + "/v1/traces", GrafanaOtlpTelemetryAdapter.TracesPath));
    }

    [Fact]
    public void EverySignalPath_IsRootedSoItJoinsCleanly()
    {
        foreach (var path in new[]
        {
            GrafanaOtlpTelemetryAdapter.TracesPath,
            GrafanaOtlpTelemetryAdapter.MetricsPath,
            GrafanaOtlpTelemetryAdapter.LogsPath,
        })
        {
            Assert.StartsWith("/", path, StringComparison.Ordinal);
        }
    }
}

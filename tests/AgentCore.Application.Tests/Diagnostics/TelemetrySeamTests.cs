using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Diagnostics;

/// <summary>
/// The fourth vendor seam: <c>providers.telemetry</c>, and what a document that names none gets.
/// </summary>
/// <remarks>
/// <para>
/// This seam differs from the other three in direction. Nothing in the library ever calls a
/// telemetry adapter, because the library writes to the <c>ActivitySource</c> and <c>Meter</c> that
/// <see cref="AgentCoreTelemetry"/> owns and an exporter listens to those two names. The adapter
/// starts a listener and holds it, which is why <see cref="ITelemetrySession"/> is disposable and
/// why disposal is the flush.
/// </para>
/// <para>
/// These tests pin the parts that are this project's and not a vendor's: which adapter a
/// <c>kind</c> picks, that a document naming none starts nothing at all, and the T61 floor under the
/// export interval that costs money to lose.
/// </para>
/// </remarks>
public sealed class TelemetrySeamTests
{
    // ---------------------------------------------------------------------------------------------
    // A document that names nothing.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task NoTelemetryBlock_StartsNothingAndAsksNoAdapter()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document());
        FakeTelemetryAdapter adapter = new("grafana");

        var session = await TelemetrySessionFactory.StartAsync(
            configuration,
            secrets: null,
            [adapter],
            TestContext.Current.CancellationToken);

        // The deliberate default. Telemetry needs a vendor account, and a library that refused to
        // start without one could not be used in a test. Registering a vendor costs nothing until a
        // document names it, so the adapter is never asked and no credential is read.
        Assert.Null(session);
        Assert.Equal(0, adapter.Starts);
    }

    // ---------------------------------------------------------------------------------------------
    // The kind picks the adapter.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheKind_PicksOneAdapterAndLeavesTheOthersAlone()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: grafana"));
        FakeTelemetryAdapter grafana = new("grafana");
        FakeTelemetryAdapter other = new("honeycomb");

        var session = await TelemetrySessionFactory.StartAsync(
            configuration,
            secrets: null,
            [other, grafana],
            TestContext.Current.CancellationToken);

        Assert.Same(grafana.Session, session);
        Assert.Equal(1, grafana.Starts);
        Assert.Equal(0, other.Starts);
    }

    [Fact]
    public async Task TheKind_MatchesWithoutRegardToCase()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: Grafana"));
        FakeTelemetryAdapter adapter = new("grafana");

        var session = await TelemetrySessionFactory.StartAsync(
            configuration,
            secrets: null,
            [adapter],
            TestContext.Current.CancellationToken);

        // A vendor name is written by a human, so a capital letter is not a different vendor.
        Assert.NotNull(session);
    }

    // ---------------------------------------------------------------------------------------------
    // A kind nobody serves fails the start, and says what is registered.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AKindNoAdapterServes_FailsTheStartAndNamesWhatIsRegistered()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: honeycomb"));
        FakeTelemetryAdapter adapter = new("grafana");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await TelemetrySessionFactory.StartAsync(
                configuration,
                secrets: null,
                [adapter],
                TestContext.Current.CancellationToken));

        // A document that asked for something this host cannot give. The message names the kinds the
        // host does register, because the reader's next move is to pick one of them.
        Assert.Contains("honeycomb", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'grafana'", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/providers/telemetry/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public async Task TwoAdaptersOnOneKind_FailTheStart()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: grafana"));

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await TelemetrySessionFactory.StartAsync(
                configuration,
                secrets: null,
                [new FakeTelemetryAdapter("grafana"), new FakeTelemetryAdapter("grafana")],
                TestContext.Current.CancellationToken));

        // One kind names one collector. Two adapters answering to it means the document silently
        // picked whichever was registered first.
        Assert.Contains("two adapters", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // T61: the export interval has a floor, and a document cannot write its way under it.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AnIntervalUnderTheFloor_IsRaisedToIt()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: grafana", "exportIntervalMs: 1000"));

        // Grafana Cloud bills the greater of active series and data points a minute, so a one-second
        // interval costs sixty times a one-minute interval for exactly the same series. This is the
        // second half of T61, the half the library cannot enforce from AgentCoreTelemetry.
        Assert.Equal(
            TelemetryProviderConfiguration.MinimumExportIntervalMilliseconds,
            configuration.Providers!.Telemetry!.ExportIntervalMilliseconds);
    }

    [Fact]
    public void AnIntervalAboveTheFloor_IsKept()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: grafana", "exportIntervalMs: 300000"));

        // A deployment may always send less often than the floor asks. Only under is corrected.
        Assert.Equal(300_000, configuration.Providers!.Telemetry!.ExportIntervalMilliseconds);
    }

    [Fact]
    public void ADocumentThatListsNoSourcesOrMeters_GetsTheBuiltInNames()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: grafana"));
        var telemetry = configuration.Providers!.Telemetry!;

        Assert.Equal(TelemetryProviderConfiguration.DefaultSources, telemetry.Sources);
        Assert.Equal(TelemetryProviderConfiguration.DefaultMeters, telemetry.Meters);
        Assert.Equal(TelemetryProviderConfiguration.DefaultServiceName, telemetry.ServiceName);
    }

    [Fact]
    public void AnEmptyListIsNotAnAbsentOne()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: grafana", "meters: []"));

        // Absent means "use the defaults" and empty means "listen to AgentCore alone". That is how a
        // deployment buys back the roughly 1,750 active series a normal replica spends against the
        // 10,000 ceiling of item 12, so the two must not collapse into each other.
        Assert.Empty(configuration.Providers!.Telemetry!.Meters);
        Assert.Equal(TelemetryProviderConfiguration.DefaultSources, configuration.Providers.Telemetry.Sources);
    }

    /// <summary>The <c>providers.knowledge</c> line the telemetry block is written after.</summary>
    private const string KnowledgeLine =
        "  knowledge: { search: filesystem, documents: filesystem, root: ./kb }";

    /// <summary>Builds the section 8.1 document with one telemetry block written into it.</summary>
    /// <param name="entries">
    /// The keys under <c>providers.telemetry</c>, one for each line and without indentation. No entry
    /// at all leaves the block out, which is the case that must start nothing.
    /// </param>
    /// <remarks>
    /// The raw string in <c>ExampleDocument</c> strips its common indentation, so inside
    /// <c>providers:</c> a key sits at two spaces and its own keys at four. Every line is written
    /// here rather than by the caller, because YAML indentation written by hand at a call site is
    /// how the first draft of these tests failed.
    /// </remarks>
    private static string Document(params string[] entries)
        => entries.Length == 0
            ? Configuration.ExampleDocument.Yaml
            : Configuration.ExampleDocument.Yaml.Replace(
                KnowledgeLine,
                KnowledgeLine + "\n  telemetry:\n" + string.Join("\n", entries.Select(entry => "    " + entry)),
                StringComparison.Ordinal);

    /// <summary>An adapter that starts nothing and records that it was asked.</summary>
    private sealed class FakeTelemetryAdapter(string kind) : ITelemetryAdapter
    {
        public string Kind => kind;

        public int Starts { get; private set; }

        public ITelemetrySession Session { get; } = new FakeSession();

        public ValueTask<ITelemetrySession> StartAsync(
            TelemetryProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            Starts++;
            return ValueTask.FromResult(Session);
        }
    }

    /// <summary>A session that exports nowhere and carries no log provider.</summary>
    private sealed class FakeSession : ITelemetrySession
    {
        public ILoggerProvider? Logs => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Audit;

/// <summary>
/// The fifth vendor seam: <c>providers.audit</c>, and what a document that names none gets.
/// </summary>
/// <remarks>
/// <para>
/// This seam differs from the other four in what an absent block means. Telemetry names no collector
/// and exports nothing, because nobody asked for an export; audit cannot do the same, because the
/// turn loop raises the D23 events whether or not the document chose a home for them, and the
/// alternative to a sink is dropping them on the floor. So the answer is never <see langword="null"/>:
/// a document that names nothing gets <see cref="InMemoryAuditSink"/>.
/// </para>
/// <para>
/// These tests pin the parts that are this project's and not a vendor's: that there is always a sink,
/// that <c>memory</c> is this library's own name and a host cannot rebind it, and that every other
/// kind goes through the same shared selector as the four seams before it.
/// </para>
/// </remarks>
public sealed class AuditSinkFactoryTests
{
    // ---------------------------------------------------------------------------------------------
    // There is always a sink.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task NoAuditBlock_GetsTheInProcessSinkAndAsksNoAdapter()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document());
        FakeAuditSinkAdapter adapter = new("postgres");

        var sink = await AuditSinkFactory.OpenAsync(
            configuration,
            secrets: null,
            [adapter],
            TestContext.Current.CancellationToken);

        // The whole point of the seam. A document that chose no store still records the call, so the
        // events of D23 have somewhere to go and a host with no database still starts.
        Assert.IsType<InMemoryAuditSink>(sink);
        Assert.Equal(0, adapter.Opens);
    }

    [Fact]
    public async Task TheMemoryKind_GetsTheInProcessSinkWithNoAdapterRegistered()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: memory"));

        var sink = await AuditSinkFactory.OpenAsync(
            configuration,
            secrets: null,
            [],
            TestContext.Current.CancellationToken);

        // Naming the built-in out loud is the same document as naming nothing, and it is served by
        // this library rather than by a vendor. A host that registered no adapter at all still starts.
        Assert.IsType<InMemoryAuditSink>(sink);
    }

    [Fact]
    public async Task TheMemoryKind_MatchesWithoutRegardToCase()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: Memory"));

        var sink = await AuditSinkFactory.OpenAsync(
            configuration,
            secrets: null,
            [],
            TestContext.Current.CancellationToken);

        // A vendor name is written by a human, so a capital letter is not a different vendor. The
        // built-in name follows the same rule the shared selector applies to every other kind.
        Assert.IsType<InMemoryAuditSink>(sink);
    }

    // ---------------------------------------------------------------------------------------------
    // Every other kind picks an adapter.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheKind_PicksOneAdapterAndLeavesTheOthersAlone()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: fake"));
        FakeAuditSinkAdapter fake = new("fake");
        FakeAuditSinkAdapter other = new("postgres");

        var sink = await AuditSinkFactory.OpenAsync(
            configuration,
            secrets: null,
            [other, fake],
            TestContext.Current.CancellationToken);

        Assert.Same(fake.Sink, sink);
        Assert.Equal(1, fake.Opens);
        Assert.Equal(0, other.Opens);
    }

    [Fact]
    public async Task AnAdapterThatClaimsTheMemoryKind_DoesNotTakeOverTheBuiltIn()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: memory"));
        FakeAuditSinkAdapter impostor = new("memory");

        var sink = await AuditSinkFactory.OpenAsync(
            configuration,
            secrets: null,
            [impostor],
            TestContext.Current.CancellationToken);

        // memory is this library's own name and is answered before the selector runs, so a document
        // that writes it means one fixed thing on every host that reads it. A host with a second
        // in-process store gives it a name of its own instead.
        Assert.IsType<InMemoryAuditSink>(sink);
        Assert.Equal(0, impostor.Opens);
    }

    // ---------------------------------------------------------------------------------------------
    // A kind nobody serves fails the start, and says what is registered.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AKindNoAdapterServes_FailsTheStartAndNamesWhatIsRegistered()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: postgres"));
        FakeAuditSinkAdapter adapter = new("fake");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await AuditSinkFactory.OpenAsync(
                configuration,
                secrets: null,
                [adapter],
                TestContext.Current.CancellationToken));

        // A document that asked for something this host cannot give. The failure belongs to the
        // start and never to a call, which is what item 9 of section 11 asks for.
        Assert.Contains("postgres", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'fake'", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/providers/audit/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public async Task TwoAdaptersOnOneKind_FailTheStart()
    {
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: postgres"));

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await AuditSinkFactory.OpenAsync(
                configuration,
                secrets: null,
                [new FakeAuditSinkAdapter("postgres"), new FakeAuditSinkAdapter("postgres")],
                TestContext.Current.CancellationToken));

        // One kind names one store. Two adapters answering to it means the chain of D23 silently
        // went to whichever was registered first, which is the one thing an audit trail cannot do.
        Assert.Contains("two adapters", failure.Message, StringComparison.Ordinal);

        // And the noun is this seam's own. VendorSeam.Plural exists to keep each seam's wording
        // through one shared selector, so audit's "sinks" is pinned here — without this, the
        // argument could be dropped and nothing would fail.
        Assert.Contains("sinks", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The <c>providers.knowledge</c> line the audit block is written after.</summary>
    private const string KnowledgeLine =
        "  knowledge: { kind: qdrant, endpoint: https://qdrant.example.com:6334, collection: kb, vector: dense, links: { lookup: uuid5 } }";

    /// <summary>Builds the section 8.1 document with one audit block written into it.</summary>
    /// <param name="entries">
    /// The keys under <c>providers.audit</c>, one for each line and without indentation. No entry at
    /// all leaves the block out, which is the case that must still produce a sink.
    /// </param>
    /// <remarks>
    /// The raw string in <c>ExampleDocument</c> strips its common indentation, so inside
    /// <c>providers:</c> a key sits at two spaces and its own keys at four. Every line is written here
    /// rather than by the caller, because YAML indentation written by hand at a call site is how the
    /// first draft of the telemetry tests failed.
    /// </remarks>
    private static string Document(params string[] entries)
        => entries.Length == 0
            ? Configuration.ExampleDocument.Yaml
            : Configuration.ExampleDocument.Yaml.Replace(
                KnowledgeLine,
                KnowledgeLine + "\n  audit:\n" + string.Join("\n", entries.Select(entry => "    " + entry)),
                StringComparison.Ordinal);

    /// <summary>An adapter that opens nothing and records that it was asked.</summary>
    private sealed class FakeAuditSinkAdapter(string kind) : IAuditSinkAdapter
    {
        public string Kind => kind;

        public int Opens { get; private set; }

        /// <summary>The store this adapter hands over. It is raw, and carries no queue of its own.</summary>
        public IAuditSinkPort Sink { get; } = new FakeAuditSink();

        public ValueTask<IAuditSinkPort> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            Opens++;
            return ValueTask.FromResult(Sink);
        }
    }

    /// <summary>A store that keeps nothing, and is a different type from the built-in one.</summary>
    /// <remarks>
    /// It is deliberately not an <see cref="InMemoryAuditSink"/>. A vendor's store answering to a
    /// vendor's kind and the built-in answering to <c>memory</c> must be told apart by type, or the
    /// test that the built-in wins would pass against a factory that never had a built-in.
    /// </remarks>
    private sealed class FakeAuditSink : IAuditSinkPort
    {
        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}

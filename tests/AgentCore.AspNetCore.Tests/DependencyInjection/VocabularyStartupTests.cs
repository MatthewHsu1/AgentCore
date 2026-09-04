using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.State;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// Task A7: section 10's boot read of every <c>vocabulary:</c> slot, K44's Unicode probe, and K48's
/// per-wildcard-facet <c>*</c> check — all made in <see cref="KnowledgeStartup.ApplyVocabularyAsync"/>.
/// </summary>
public sealed class VocabularyStartupTests
{
    [Fact]
    public async Task ApplyVocabularyAsync_ZeroValues_FailsNamingTheSlotAndPath()
    {
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10))]);
        FakeFacetPort port = new();
        VocabularyCache cache = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => KnowledgeStartup.ApplyVocabularyAsync(
                configuration, port, cache, NullLogger.Instance, TestContext.Current.CancellationToken,
                composesUnicode: () => true).AsTask());

        var error = Assert.Single(failure.Errors);
        Assert.Contains("brand", error.Message, StringComparison.Ordinal);
        Assert.Contains("facets.brand", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_ExactlyMaxValues_FailsAsTruncated()
    {
        var configuration = Configuration([("brand", Vocabulary(maxValues: 3))]);
        var port = new FakeFacetPort().With("facets.brand", "acme", "globex", "initech");
        VocabularyCache cache = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => KnowledgeStartup.ApplyVocabularyAsync(
                configuration, port, cache, NullLogger.Instance, TestContext.Current.CancellationToken,
                composesUnicode: () => true).AsTask());

        var error = Assert.Single(failure.Errors);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
        Assert.Contains("brand", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_TwoValuesFoldAlike_FailsNamingBoth()
    {
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10))]);
        var port = new FakeFacetPort().With("facets.brand", "Acme", "ACME");
        VocabularyCache cache = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => KnowledgeStartup.ApplyVocabularyAsync(
                configuration, port, cache, NullLogger.Instance, TestContext.Current.CancellationToken,
                composesUnicode: () => true).AsTask());

        var error = Assert.Single(failure.Errors);
        Assert.Contains("Acme", error.Message, StringComparison.Ordinal);
        Assert.Contains("ACME", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_ExactlyOneValue_SucceedsAndWarns()
    {
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10))]);
        var port = new FakeFacetPort().With("facets.brand", "acme");
        VocabularyCache cache = new();
        RecordingLoggerFactory loggers = new();

        await KnowledgeStartup.ApplyVocabularyAsync(
            configuration, port, cache, loggers.CreateLogger("test"), TestContext.Current.CancellationToken,
            composesUnicode: () => true);

        Assert.Equal(["acme"], cache.Snapshot()["brand"].Originals);
        var line = Assert.Single(loggers.Of(1));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, line.Level);
        Assert.Equal("brand", line.Field<string>("Slot"));
    }

    [Fact]
    public async Task ApplyVocabularyAsync_NonComposingRuntimeWithoutAssumeNormalized_FailsNamingInvariantGlobalization()
    {
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10, assumeNormalized: false))]);
        var port = new FakeFacetPort().With("facets.brand", "acme", "globex");
        VocabularyCache cache = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => KnowledgeStartup.ApplyVocabularyAsync(
                configuration, port, cache, NullLogger.Instance, TestContext.Current.CancellationToken,
                composesUnicode: () => false).AsTask());

        var error = Assert.Single(failure.Errors);
        Assert.Contains("InvariantGlobalization", error.Message, StringComparison.Ordinal);
        Assert.Contains("brand", error.Message, StringComparison.Ordinal);
        Assert.Empty(port.Reads);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_NonComposingRuntimeWithAssumeNormalized_Succeeds()
    {
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10, assumeNormalized: true))]);
        var port = new FakeFacetPort().With("facets.brand", "acme", "globex");
        VocabularyCache cache = new();

        await KnowledgeStartup.ApplyVocabularyAsync(
            configuration, port, cache, NullLogger.Instance, TestContext.Current.CancellationToken,
            composesUnicode: () => false);

        Assert.Equal(["acme", "globex"], cache.Snapshot()["brand"].Originals);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_ComposingRuntime_Succeeds()
    {
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10, assumeNormalized: false))]);
        var port = new FakeFacetPort().With("facets.brand", "acme", "globex");
        VocabularyCache cache = new();

        await KnowledgeStartup.ApplyVocabularyAsync(
            configuration, port, cache, NullLogger.Instance, TestContext.Current.CancellationToken,
            composesUnicode: () => true);

        Assert.Equal(["acme", "globex"], cache.Snapshot()["brand"].Originals);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_WildcardFacetWithNoStar_LogsK48NamingTheFacet()
    {
        var configuration = Configuration(
            [("brand", Vocabulary(maxValues: 10))],
            wildcard: new KnowledgeWildcardConfiguration { Value = "*", Facets = ["brand"] });
        var port = new FakeFacetPort().With("facets.brand", "acme", "globex");
        VocabularyCache cache = new();
        RecordingLoggerFactory loggers = new();

        await KnowledgeStartup.ApplyVocabularyAsync(
            configuration, port, cache, loggers.CreateLogger("test"), TestContext.Current.CancellationToken,
            composesUnicode: () => true);

        var line = Assert.Single(loggers.Of(2));
        Assert.Equal("brand", line.Field<string>("Facet"));
        Assert.Contains(2, port.Reads.Where(r => r.Path == "facets.brand").Select(r => r.Limit));
    }

    [Fact]
    public async Task ApplyVocabularyAsync_WildcardFacetWithStar_NoWarning()
    {
        var configuration = Configuration(
            [("brand", Vocabulary(maxValues: 10))],
            wildcard: new KnowledgeWildcardConfiguration { Value = "*", Facets = ["brand"] });

        // The vocabulary read itself carries the wildcard (stripped by K6); the K48 read is a
        // separate, small read that must see it.
        var port = new FakeFacetPort().With("facets.brand", "acme", "globex", "*");
        VocabularyCache cache = new();
        RecordingLoggerFactory loggers = new();

        await KnowledgeStartup.ApplyVocabularyAsync(
            configuration, port, cache, loggers.CreateLogger("test"), TestContext.Current.CancellationToken,
            composesUnicode: () => true);

        Assert.Empty(loggers.Of(2));
    }

    [Fact]
    public async Task ApplyVocabularyAsync_WildcardOnlyDocumentAndFacetAwarePortThrowsOnTheK48Read_WarnsAndBootSucceeds()
    {
        // K48 sits under "warned, not refused": a wildcard-only document (no vocabulary: slot
        // anywhere) still reaches this read once its port is facet-aware (Task A4), and a store that
        // cannot serve the small K48 sample -- for example, no keyword payload index at this path on
        // Qdrant -- must not turn a document that was booting into one that cannot.
        var configuration = Configuration(
            wildcard: new KnowledgeWildcardConfiguration { Value = "*", Facets = ["applies_to"] },
            extraFromState: ["applies_to"]);
        var port = new FakeFacetPort().ThrowsOn(
            "facets.applies_to", new InvalidOperationException("no keyword payload index at this path"));
        VocabularyCache cache = new();
        RecordingLoggerFactory loggers = new();

        await KnowledgeStartup.ApplyVocabularyAsync(
            configuration, port, cache, loggers.CreateLogger("test"), TestContext.Current.CancellationToken);

        var line = Assert.Single(loggers.Of(3));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("applies_to", line.Field<string>("Facet"));
        Assert.Empty(cache.Snapshot());
    }

    [Fact]
    public void ComposesUnicode_UnderThisTestHostsInvariantGlobalization_IsFalse()
    {
        // Nothing else pins this method itself: every other row drives the outcome through the
        // composesUnicode override seam, so a regression that made ComposesUnicode() always report
        // true (for example, swapping its escaped "Å" literal for typed characters, which
        // reads identically but is pre-composed by the time the source file reaches disk) would pass
        // every one of them. This project runs under InvariantGlobalization=true, where
        // string.Normalize is a no-op, so the real method must report false here.
        Assert.False(KnowledgeStartup.ComposesUnicode());
    }

    [Fact]
    public async Task ApplyVocabularyAsync_NoVocabularyAndNoWildcardFacets_DoesNothing()
    {
        var configuration = Configuration();
        FakeFacetPort port = new();
        VocabularyCache cache = new();

        await KnowledgeStartup.ApplyVocabularyAsync(
            configuration, port, cache, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Empty(port.Reads);
        Assert.Empty(cache.Snapshot());
    }

    [Fact]
    public async Task ApplyVocabularyAsync_HostBoundPortNotFacetAware_FailsNamingIFacetVocabularyPort()
    {
        // K27's first exit: options.UseKnowledgeRetrieval names a port of the host's own, and that
        // port does not implement IFacetVocabularyPort.
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10))]);
        HostBoundPort knowledge = new();
        VocabularyCache cache = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => KnowledgeStartup.ApplyVocabularyAsync(
                configuration, knowledge, cache, NullLogger.Instance, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(nameof(IFacetVocabularyPort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_CompositeBuiltPortNotFacetAware_FailsNamingIFacetVocabularyPort()
    {
        // K27's second exit: CompositeKnowledgeStoreFactory matched providers.knowledge.kind to an
        // adapter whose port also does not implement IFacetVocabularyPort.
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10))]);
        CompositeBuiltPort knowledge = new();
        VocabularyCache cache = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => KnowledgeStartup.ApplyVocabularyAsync(
                configuration, knowledge, cache, NullLogger.Instance, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(nameof(IFacetVocabularyPort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_NoPortAtAll_FailsNamingIFacetVocabularyPort()
    {
        // K27's third exit: no adapter was registered, so KnowledgeStartup.OpenAsync returns null.
        var configuration = Configuration([("brand", Vocabulary(maxValues: 10))]);
        VocabularyCache cache = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => KnowledgeStartup.ApplyVocabularyAsync(
                configuration, knowledge: null, cache, NullLogger.Instance, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(nameof(IFacetVocabularyPort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyVocabularyAsync_WildcardOnlyDocumentAndPortNotFacetAware_SkipsSilently()
    {
        // A pre-existing wildcard-plan document (no vocabulary: slot anywhere) must not suddenly
        // require IFacetVocabularyPort just because it uses wildcard.facets -- K27's refusal is
        // scoped to a document that declares vocabulary:.
        var configuration = Configuration(
            wildcard: new KnowledgeWildcardConfiguration { Value = "*", Facets = ["applies_to"] },
            extraFromState: ["applies_to"]);
        HostBoundPort knowledge = new();
        VocabularyCache cache = new();

        await KnowledgeStartup.ApplyVocabularyAsync(
            configuration, knowledge, cache, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Empty(cache.Snapshot());
    }

    private static SlotVocabularyConfiguration Vocabulary(int maxValues, bool assumeNormalized = false)
        => new() { From = "knowledge", MaxValues = maxValues, AssumeNormalized = assumeNormalized };

    private static AgentCoreConfiguration Configuration(
        (string Slot, SlotVocabularyConfiguration Vocabulary)[]? vocabularySlots = null,
        KnowledgeWildcardConfiguration? wildcard = null,
        IReadOnlyList<string>? extraFromState = null)
    {
        vocabularySlots ??= [];
        extraFromState ??= [];

        Dictionary<string, StateSlotConfiguration> state = new(StringComparer.Ordinal);
        foreach (var (slot, vocabulary) in vocabularySlots)
        {
            state[slot] = new StateSlotConfiguration
            {
                Type = StateSlotType.String,
                Writer = StateWriter.Extractor,
                Vocabulary = vocabulary,
            };
        }

        foreach (var slot in extraFromState)
        {
            state.TryAdd(slot, new StateSlotConfiguration { Type = StateSlotType.String, Writer = StateWriter.Extractor });
        }

        List<string> fromState = [.. vocabularySlots.Select(entry => entry.Slot), .. extraFromState];

        return new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "vocabulary-startup",
            State = state,
            Providers = new ProvidersConfiguration
            {
                Knowledge = new KnowledgeProviderConfiguration
                {
                    Kind = "test",
                    Collection = "kb",
                    Fields = new KnowledgeFieldsConfiguration { Body = "body" },
                    Scope = new KnowledgeScopeConfiguration
                    {
                        Template = "facets.{key}",
                        FromState = fromState,
                        Wildcard = wildcard,
                    },
                },
            },
        };
    }

    /// <summary>A live port that records every read, and answers a canned map by path.</summary>
    private sealed class FakeFacetPort : IKnowledgeRetrievalPort, IFacetVocabularyPort
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _throwsByPath = new(StringComparer.Ordinal);

        public List<(string Path, int Limit)> Reads { get; } = [];

        public FakeFacetPort With(string path, params string[] values)
        {
            _byPath[path] = values;
            return this;
        }

        public FakeFacetPort ThrowsOn(string path, Exception exception)
        {
            _throwsByPath[path] = exception;
            return this;
        }

        public ValueTask<IReadOnlyList<string>> ReadAsync(
            string path, int limit, CancellationToken cancellationToken = default)
        {
            Reads.Add((path, limit));

            if (_throwsByPath.TryGetValue(path, out var exception))
            {
                throw exception;
            }

            return ValueTask.FromResult(_byPath.TryGetValue(path, out var values) ? values : (IReadOnlyList<string>)[]);
        }

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }

    /// <summary>Stands in for a port <c>options.UseKnowledgeRetrieval</c> named, with no facet read.</summary>
    private sealed class HostBoundPort : IKnowledgeRetrievalPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }

    /// <summary>Stands in for a port <c>CompositeKnowledgeStoreFactory</c> built, with no facet read.</summary>
    private sealed class CompositeBuiltPort : IKnowledgeRetrievalPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }
}

/// <summary>
/// The rest of task A7's wiring, exercised through <see cref="AgentCoreBoot.BootAsync"/> itself: the
/// configuration warnings logged below telemetry, <c>ValidateLinkerNames</c> run after the linker
/// registry is built, the vocabulary read reaching a real adapter-built port, and the background
/// refresh started.
/// </summary>
public sealed class AgentCoreBootVocabularyTests
{
    [Fact]
    public async Task BootAsync_ASingleFacetAmbiguityDocument_LogsTheConfigurationWarningBelowTelemetry()
    {
        // R1's actual plumbing: a warning EvaluateStructure raised (K33 here) must reach the log,
        // through ConfigurationStartup.Load's returned Warnings and AgentCoreBoot's own logger.
        RecordingLoggerFactory loggers = new();
        FacetKnowledgeAdapter adapter = new();
        adapter.Port.With("facets.machine", "ct900", "ct1200");

        await using var boot = Boot(Document(Vocabulary(10)), adapter, loggers);
        await boot.BootAsync(TestContext.Current.CancellationToken);

        var warnings = loggers.Lines.Where(line =>
            line.Level == LogLevel.Warning
            && line.Message.Contains("/providers/knowledge/ambiguity", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(warnings, line => line.Message.Contains("at most one", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_AGraphDocumentWithAmbiguity_ReturnsTheK39Warning()
    {
        // K39: channel 1 cannot fire on a graph: document, so ambiguity: there is a boot warning
        // (CheckVocabularyAndAmbiguity, task A1) that ConfigurationStartup.Load must not discard.
        var wildcard = new KnowledgeWildcardConfiguration { Value = "*", Facets = ["applies_to", "brand"] };
        StateSlotConfiguration facet = new() { Type = StateSlotType.String, Writer = StateWriter.Extractor, EnumValues = [] };

        var configuration = new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "graph-ambiguity",
            Graph = new GraphConfiguration(),
            Extractor = new ExtractorConfiguration { Model = new ModelReference { Ref = "small" } },
            State = new Dictionary<string, StateSlotConfiguration>(StringComparer.Ordinal)
            {
                ["applies_to"] = facet with { EnumValues = [JsonValue.Create("a")!] },
                ["brand"] = facet with { EnumValues = [JsonValue.Create("b")!] },
            },
            Providers = new ProvidersConfiguration
            {
                Llm = [new LlmProviderConfiguration { Kind = "openai", Model = "gpt", As = "small" }],
                Knowledge = new KnowledgeProviderConfiguration
                {
                    Kind = "qdrant",
                    Collection = "kb",
                    Fields = new KnowledgeFieldsConfiguration { Body = "text" },
                    Scope = new KnowledgeScopeConfiguration
                    {
                        Template = "facets.{key}",
                        FromState = ["applies_to", "brand"],
                        Wildcard = wildcard,
                    },
                    Ambiguity = new KnowledgeAmbiguityConfiguration(),
                },
            },
        };

        AgentCoreOptions options = new() { Configuration = configuration };

        var result = ConfigurationStartup.Load(options);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("/providers/knowledge/ambiguity", warning.Pointer);
    }

    [Fact]
    public async Task BootAsync_AZeroValueVocabularyRead_FailsBootNamingTheSlot()
    {
        FacetKnowledgeAdapter adapter = new();

        // No .With(...) call: the fake answers every path with an empty list.
        await using var boot = Boot(Document(Vocabulary(10)), adapter);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => boot.BootAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("machine", failure.Message, StringComparison.Ordinal);
        Assert.True(adapter.Port.Reads.Count > 0);
    }

    [Fact]
    public async Task BootAsync_ABadLinkerName_FailsBootBeforeReadingTheVocabulary()
    {
        FacetKnowledgeAdapter adapter = new();
        adapter.Port.With("facets.machine", "ct900", "ct1200");

        await using var boot = Boot(Document(Vocabulary(10, linker: "bogus")), adapter);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => boot.BootAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("bogus", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootAsync_AHostRegisteredLinkerName_Succeeds()
    {
        FacetKnowledgeAdapter adapter = new();
        adapter.Port.With("facets.machine", "ct900", "ct1200");

        await using var boot = Boot(
            Document(Vocabulary(10, linker: "custom")), adapter, linkers: [new NamedLinker("custom")]);

        await boot.BootAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(boot.Sessions);
    }

    [Fact]
    public async Task BootAsync_AHostRegisteredLinkerName_ItsResultReachesTheExtractedState()
    {
        // "Boot succeeds" (the fact above) would have passed against the exact bug this fact exists
        // to catch: CallSessionFactory hard-coded an empty linker registry, so a host's own linker
        // validated by name at boot and was then never actually consulted. Only running a real turn
        // and reading back what the CUSTOM linker -- not exact, not nothing -- wrote proves the
        // registry travelled all the way from UseStateValueLinkers into the extractor.
        FacetKnowledgeAdapter adapter = new();
        adapter.Port.With("facets.machine", "ct900", "ct1200");

        var configuration = Document(Vocabulary(10, linker: "custom"));

        AgentCoreOptions options = new() { Configuration = configuration };
        options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("okay, noted"))
            .Route("fill", new FragmentingChatClient("""{"machine":"ct900"}""")));
        options.UseKnowledgeStores(adapter);
        options.UseStateValueLinkers(new FixedResultLinker("custom", "ct1200"));

        await using var boot = new AgentCoreBoot(Options.Create(options), NullLoggerFactory.Instance);
        await boot.BootAsync(TestContext.Current.CancellationToken);

        var session = boot.Sessions.Create();
        await session.RunTurnAsync("what machine do I have", TestContext.Current.CancellationToken);

        // exact would resolve the mention "ct900" to "ct900" itself; the custom linker ignores the
        // mention and always answers "ct1200" -- chosen so the two are never accidentally the same.
        Assert.Equal("ct1200", session.State.Read("machine")?.GetValue<string>());
    }

    [Fact]
    public async Task BootAsync_ValidVocabularyDocument_BuildsAWorkingSessionFactory()
    {
        FacetKnowledgeAdapter adapter = new();
        adapter.Port.With("facets.machine", "ct900", "ct1200");

        await using var boot = Boot(Document(Vocabulary(10)), adapter);
        await boot.BootAsync(TestContext.Current.CancellationToken);

        var session = boot.Sessions.Create();
        Assert.NotNull(session);
    }

    [Fact]
    public async Task BootAsync_ARefreshIntervalConfigured_StartsTheBackgroundRefresh()
    {
        FacetKnowledgeAdapter adapter = new();
        adapter.Port.With("facets.machine", "ct900", "ct1200");
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);

        await using var boot = Boot(Document(Vocabulary(10, refreshSeconds: 60)), adapter, timeProvider: time);
        await boot.BootAsync(TestContext.Current.CancellationToken);

        var readsAtBoot = adapter.Port.Reads.Count;
        Assert.True(readsAtBoot > 0);

        for (var attempt = 0; attempt < 200 && adapter.Port.Reads.Count <= readsAtBoot; attempt++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(adapter.Port.Reads.Count > readsAtBoot);
    }

    private static SlotVocabularyConfiguration Vocabulary(int maxValues, string? linker = null, int refreshSeconds = 0)
        => new()
        {
            From = "knowledge",
            MaxValues = maxValues,
            Linker = linker ?? SlotVocabularyConfiguration.DefaultLinker,
            RefreshSeconds = refreshSeconds,

            // K44 is covered at the ApplyVocabularyAsync unit level (composesUnicode seam). These
            // boot-level facts are about other wiring, and the AspNetCore test host really does run
            // under InvariantGlobalization=true, so without this every one of them would hit K44's
            // refusal first.
            AssumeNormalized = true,
        };

    private static AgentCoreConfiguration Document(SlotVocabularyConfiguration vocabulary)
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "vocabulary-boot",
            Extractor = new ExtractorConfiguration { Model = new ModelReference { Ref = "fill" } },
            State = new Dictionary<string, StateSlotConfiguration>(StringComparer.Ordinal)
            {
                ["machine"] = new StateSlotConfiguration
                {
                    Type = StateSlotType.String,
                    Writer = StateWriter.Extractor,
                    Vocabulary = vocabulary,
                },
            },
            Providers = new ProvidersConfiguration
            {
                Llm = [new LlmProviderConfiguration { Kind = "test", Model = "test", As = "fill" }],
                Knowledge = new KnowledgeProviderConfiguration
                {
                    Kind = "test",
                    Collection = "kb",
                    Fields = new KnowledgeFieldsConfiguration { Body = "body" },
                    Scope = new KnowledgeScopeConfiguration
                    {
                        Template = "facets.{key}",
                        FromState = ["machine"],
                        Wildcard = new KnowledgeWildcardConfiguration { Value = "*", Facets = ["machine"] },
                    },
                    Ambiguity = new KnowledgeAmbiguityConfiguration(),
                },
            },
            Policy = new PolicyConfiguration
            {
                Initial = "answering",
                Stages = [new StageConfiguration { Id = "answering", Agent = "resolver", Terminal = true }],
            },
            Agents = new AgentsConfiguration
            {
                Items =
                [
                    new AgentConfiguration
                    {
                        Id = "resolver",
                        Instructions = "I answer about one machine",
                        Knowledge = new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch, Scoped = true },
                    },
                ],
            },
        };

    private static AgentCoreBoot Boot(
        AgentCoreConfiguration configuration,
        FacetKnowledgeAdapter adapter,
        RecordingLoggerFactory? loggers = null,
        TimeProvider? timeProvider = null,
        IStateValueLinker[]? linkers = null)
    {
        AgentCoreOptions options = new()
        {
            Configuration = configuration,
            LoggerFactory = loggers,
            TimeProvider = timeProvider,
        };

        options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
        options.UseKnowledgeStores(adapter);

        if (linkers is { Length: > 0 })
        {
            options.UseStateValueLinkers(linkers);
        }

        return new AgentCoreBoot(Options.Create(options), (ILoggerFactory?)loggers ?? NullLoggerFactory.Instance);
    }

    /// <summary>Answers <c>providers.knowledge.kind: test</c> with a live facet-aware port.</summary>
    private sealed class FacetKnowledgeAdapter : IKnowledgeStoreAdapter
    {
        public string Kind => "test";

        public bool CanServeSearch => true;

        public bool CanScope => true;

        public FacetKnowledgePort Port { get; } = new();

        public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
            KnowledgeProviderConfiguration entry,
            ISecretResolverPort? secrets,
            IEmbeddingGenerator<string, Embedding<float>>? embeddings,
            bool requireScope,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IKnowledgeRetrievalPort>(Port);
    }

    /// <summary>A live port that records every read, and answers a canned map by path.</summary>
    private sealed class FacetKnowledgePort : IKnowledgeRetrievalPort, IFacetVocabularyPort
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _byPath = new(StringComparer.Ordinal);

        public List<(string Path, int Limit)> Reads { get; } = [];

        public FacetKnowledgePort With(string path, params string[] values)
        {
            _byPath[path] = values;
            return this;
        }

        public ValueTask<IReadOnlyList<string>> ReadAsync(
            string path, int limit, CancellationToken cancellationToken = default)
        {
            lock (Reads)
            {
                Reads.Add((path, limit));
            }

            return ValueTask.FromResult(_byPath.TryGetValue(path, out var values) ? values : (IReadOnlyList<string>)[]);
        }

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }

    /// <summary>A linker that never links, registered under a host-chosen name.</summary>
    private sealed class NamedLinker(string name) : IStateValueLinker
    {
        public string Name { get; } = name;

        public LinkResult Link(string mention, VocabularyView vocabulary, IReadOnlySet<string> lastNamed)
            => new(LinkOutcome.NoMatch, []);
    }

    /// <summary>
    /// Always links to the same value, ignoring the mention. Distinguishable on purpose: no reading
    /// of this test can mistake its output for <c>exact</c>'s.
    /// </summary>
    private sealed class FixedResultLinker(string name, string result) : IStateValueLinker
    {
        public string Name { get; } = name;

        public LinkResult Link(string mention, VocabularyView vocabulary, IReadOnlySet<string> lastNamed)
            => new(LinkOutcome.Linked, [result]);
    }
}

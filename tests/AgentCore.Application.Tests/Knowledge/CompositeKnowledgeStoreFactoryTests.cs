using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Knowledge.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The composite behind <c>UseKnowledgeStores</c>. It routes <c>providers.knowledge.kind</c> to the
/// adapter whose <see cref="IKnowledgeStoreAdapter.Kind"/> matches.
/// </summary>
/// <remarks>
/// Every adapter here is a fake, so every test runs offline. The document names the vendor and the
/// host registers the adapters; these tests prove the document alone decides which adapter answers.
/// </remarks>
public sealed class CompositeKnowledgeStoreFactoryTests
{
    private const string NoKnowledgeYaml =
        """
        apiVersion: agentcore/v1
        name: no-knowledge
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    private const string OneKindYaml =
        """
        apiVersion: agentcore/v1
        name: one-store
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            endpoint: https://cluster.example.com
            collection: manuals
            fields: { body: body }
        """;

    private const string ShoutedKindYaml =
        """
        apiVersion: agentcore/v1
        name: shouted-store
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: QDRANT
            collection: manuals
            fields: { body: body }
        """;

    // ---------------------------------------------------------------------------------------------
    // Routing: the document names the vendor, and the matching adapter builds it.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AKindWrittenInAnotherCase_StillFindsItsAdapter()
    {
        RecordingKnowledgeStoreAdapter qdrant = new("qdrant");

        var port = await Create(ShoutedKindYaml, qdrant);

        Assert.Same(qdrant.Search, port);
    }

    [Fact]
    public async Task NoKnowledgeBlockAtAll_FailsWithAPointerAtTheBlock()
    {
        // There is no entry to invent. A standing-in default would guess the vendor, the collection
        // name AND the payload shape, then fail somewhere further in against a store nobody named.
        RecordingKnowledgeStoreAdapter qdrant = new("qdrant");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(NoKnowledgeYaml, qdrant));

        Assert.Contains(failure.Errors, error => error.Pointer == "/providers/knowledge");
        Assert.False(qdrant.CreateSearchCalled);
    }

    [Fact]
    public async Task TheAdapter_ReceivesTheGeneratorTheHostBuilt()
    {
        RecordingKnowledgeStoreAdapter qdrant = new("qdrant");
        Embeddings.Fakes.FakeEmbeddingGenerator embeddings = new();

        await CompositeKnowledgeStoreFactory.CreateAsync(
            ConfigurationLoader.LoadYaml(OneKindYaml),
            secrets: null,
            [qdrant],
            embeddings,
            scopeDeclared: false,
            requireScope: false,
            TestContext.Current.CancellationToken);

        Assert.Same(embeddings, qdrant.LastEmbeddings);
    }

    [Fact]
    public async Task TheAdapter_ReceivesTheEntryAndTheResolverChainTheHostBound()
    {
        RecordingKnowledgeStoreAdapter qdrant = new("qdrant");
        MapSecretResolver resolver = new();

        await Create(OneKindYaml, resolver, qdrant);

        Assert.Same(resolver, qdrant.LastSecrets);
        Assert.Equal("manuals", qdrant.LastEntry?.Collection);
    }

    [Fact]
    public async Task TheAdapter_ReceivesRequireScopeSeparatelyFromScopeDeclared()
    {
        // Ruling 14(a): requireScope (ALL agents scoped) is a different question from the
        // scopeDeclared this factory already validates CanScope against (ANY agent scoped), and it
        // must reach the adapter even when scopeDeclared itself is false.
        RecordingKnowledgeStoreAdapter qdrant = new("qdrant") { CanScope = true };

        var port = await CompositeKnowledgeStoreFactory.CreateAsync(
            Document(kind: "qdrant"), secrets: null, [qdrant], embeddings: null, scopeDeclared: false, requireScope: true, CancellationToken.None);

        Assert.NotNull(port);
        Assert.True(qdrant.LastRequireScope);
    }

    // ---------------------------------------------------------------------------------------------
    // What it refuses, at startup and never on the first call.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AKindNoAdapterServes_FailsAndNamesTheRegisteredKinds()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(OneKindYaml, new RecordingKnowledgeStoreAdapter("filesystem")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/kind", error.Pointer);
        Assert.Contains("qdrant", error.Message, StringComparison.Ordinal);
        Assert.Contains("'filesystem'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdapterThatDoesNotServeSearch_FailsAndNamesThePort()
    {
        RecordingKnowledgeStoreAdapter qdrant = new("qdrant") { CanServeSearch = false };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(OneKindYaml, qdrant));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/kind", error.Pointer);
        Assert.Contains(nameof(IKnowledgeRetrievalPort), error.Message, StringComparison.Ordinal);
        Assert.False(qdrant.CreateSearchCalled);
    }

    [Fact]
    public async Task TwoAdaptersOfOneKind_FailTheBuild()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () => await Create(
            OneKindYaml,
            new RecordingKnowledgeStoreAdapter("qdrant"),
            new RecordingKnowledgeStoreAdapter("QDrant")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/kind", error.Pointer);
        Assert.Contains("qdrant", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostThatRegistersNoAdapter_FailsAndSaysSo()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(OneKindYaml));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/kind", error.Pointer);
        Assert.Contains("no adapter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CitationsWithBothCitationFieldsDisabled_FailsTheLoadPointingAtFieldsSource()
    {
        const string yaml =
            """
            apiVersion: agentcore/v1
            name: cited
            agents:
              items:
                - id: only
                  instructions: "I answer everything"
                  knowledge: { citations: true }
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                collection: manuals
                fields: { body: body, source: null, locator: null }
            """;
        RecordingKnowledgeStoreAdapter qdrant = new("qdrant");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(yaml, qdrant));

        Assert.Equal("/providers/knowledge/fields/source", failure.Pointer);
        Assert.False(qdrant.CreateSearchCalled);
    }

    [Fact]
    public async Task CitationsWithOnlyTheLocatorMapped_StillLoads()
    {
        const string yaml =
            """
            apiVersion: agentcore/v1
            name: cited-locator
            agents:
              items:
                - id: only
                  instructions: "I answer everything"
                  knowledge: { citations: true }
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                collection: manuals
                fields: { body: body, locator: source.locator, source: null }
            """;
        RecordingKnowledgeStoreAdapter qdrant = new("qdrant");

        var port = await Create(yaml, qdrant);

        Assert.NotNull(port);
    }

    // ---------------------------------------------------------------------------------------------
    // The CanScope startup rule.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task CreateAsync_ScopeDeclaredAndAdapterCannotScope_FailsTheStart()
    {
        var configuration = Document(kind: "flat");
        var adapter = new RecordingKnowledgeStoreAdapter("flat") { CanScope = false };

        var thrown = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await CompositeKnowledgeStoreFactory.CreateAsync(
                configuration, secrets: null, [adapter], embeddings: null, scopeDeclared: true, requireScope: true, CancellationToken.None));

        Assert.Contains("/providers/knowledge/kind", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("cannot apply a scope", thrown.Message, StringComparison.Ordinal);
        Assert.False(adapter.CreateSearchCalled);
    }

    [Fact]
    public async Task CreateAsync_ScopeDeclaredAndAdapterCanScope_Opens()
    {
        var configuration = Document(kind: "scoped");
        var adapter = new RecordingKnowledgeStoreAdapter("scoped") { CanScope = true };

        var port = await CompositeKnowledgeStoreFactory.CreateAsync(
            configuration, secrets: null, [adapter], embeddings: null, scopeDeclared: true, requireScope: true, CancellationToken.None);

        Assert.NotNull(port);
        Assert.True(adapter.CreateSearchCalled);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    /// <summary>Builds a document whose only interesting field is <c>providers.knowledge.kind</c>.</summary>
    /// <remarks>
    /// The rest of the block is here because the composite requires it, not because these tests are
    /// about it: a body to map, and a scope template for the tests that declare a scope. Neither has
    /// a default to fall back on.
    /// </remarks>
    private static AgentCoreConfiguration Document(string kind)
        => new()
        {
            ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
            Name = "test",
            Providers = new ProvidersConfiguration
            {
                Knowledge = new KnowledgeProviderConfiguration
                {
                    Kind = kind,
                    Collection = "manuals",
                    Fields = new KnowledgeFieldsConfiguration { Body = "body" },
                    Scope = new KnowledgeScopeConfiguration { Template = "facets.{key}" },
                },
            },
        };

    private static ValueTask<IKnowledgeRetrievalPort?> Create(
        string yaml,
        params IKnowledgeStoreAdapter[] adapters)
        => Create(yaml, null, adapters);

    private static ValueTask<IKnowledgeRetrievalPort?> Create(
        string yaml,
        ISecretResolverPort? secrets,
        params IKnowledgeStoreAdapter[] adapters)
        => CompositeKnowledgeStoreFactory.CreateAsync(
            ConfigurationLoader.LoadYaml(yaml),
            secrets,
            adapters,
            embeddings: null,
            scopeDeclared: false,
            requireScope: false,
            TestContext.Current.CancellationToken);
}

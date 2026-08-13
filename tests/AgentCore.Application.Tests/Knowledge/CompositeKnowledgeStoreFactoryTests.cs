using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Application.Tests.Secrets.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The composite behind <c>UseKnowledgeStores</c>. It routes <c>providers.knowledge.search</c> and
/// <c>providers.knowledge.documents</c> to the adapters whose <see cref="IKnowledgeStoreAdapter.Kind"/>
/// matches, and it opens one store when both fields name one kind.
/// </summary>
/// <remarks>
/// Every adapter here is a fake, so every test runs offline. The document names the vendor and the
/// host registers the adapters; these tests prove the document alone decides which adapter answers
/// which port.
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
          knowledge:
            search: filesystem
            documents: filesystem
            root: ./kb
        """;

    private const string TwoKindsYaml =
        """
        apiVersion: agentcore/v1
        name: two-stores
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          knowledge:
            search: zilliz
            documents: filesystem
            endpoint: https://cluster.example.com
            collection: kb_chunks
        """;

    private const string ShoutedKindYaml =
        """
        apiVersion: agentcore/v1
        name: shouted-store
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          knowledge:
            search: FileSystem
            documents: FILESYSTEM
        """;

    // ---------------------------------------------------------------------------------------------
    // Routing: the document names the vendor of each port, and the matching adapter builds it.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TwoKinds_RouteToTheTwoAdaptersTheHostRegistered()
    {
        FakeKnowledgeStoreAdapter zilliz = new("zilliz") { CanServeDocuments = false, ReadsWhatItRanks = false };
        FakeKnowledgeStoreAdapter files = new("filesystem");

        var (search, documents) = await Create(TwoKindsYaml, zilliz, files);

        // This is the shape the Zilliz connector arrives in: one adapter ranks and another reads.
        Assert.Same(zilliz.Search, search);
        Assert.Same(files.Documents, documents);
        Assert.Equal(0, zilliz.DocumentBuilds);
        Assert.Equal(0, files.SearchBuilds);
    }

    [Fact]
    public async Task AKindWrittenInAnotherCase_StillFindsItsAdapter()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem");

        var (search, documents) = await Create(ShoutedKindYaml, files);

        Assert.Same(files.Search, search);
        Assert.Same(files.Search, documents);
    }

    [Fact]
    public async Task NoKnowledgeBlockAtAll_TakesTheDefaultOfBothPorts()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem");

        var (search, documents) = await Create(NoKnowledgeYaml, files);

        // Both defaults are 'filesystem', so a document that says nothing still binds both ports.
        Assert.NotNull(search);
        Assert.NotNull(documents);
        Assert.Equal(1, files.SearchBuilds);
    }

    [Fact]
    public async Task TheAdapter_ReceivesTheEntryAndTheResolverChainTheHostBound()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem");
        MapSecretResolver resolver = new();

        await Create(OneKindYaml, resolver, files);

        Assert.Same(resolver, files.LastSecrets);
        Assert.Equal("./kb", files.LastEntry?.Root);
    }

    // ---------------------------------------------------------------------------------------------
    // One kind for both ports opens one store, exactly as the UseKnowledge overload did.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task OneKindForBothPorts_OpensOneStoreAndBindsItTwice()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem");

        var (search, documents) = await Create(OneKindYaml, files);

        Assert.Same(search, documents);
        Assert.Equal(1, files.SearchBuilds);
        Assert.Equal(0, files.DocumentBuilds);
    }

    [Fact]
    public async Task OneKindWhoseRankerDoesNotRead_StillOpensTheDocumentStore()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem") { ReadsWhatItRanks = false };

        var (search, documents) = await Create(OneKindYaml, files);

        // The memoization reuses the ranked object only when that object reads as well.
        Assert.NotSame(search, documents);
        Assert.Equal(1, files.SearchBuilds);
        Assert.Equal(1, files.DocumentBuilds);
    }

    // ---------------------------------------------------------------------------------------------
    // A port the caller already holds is read nowhere. This is what makes the precedence rule of
    // UseKnowledgeStores cost nothing.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task APortTheCallerDidNotAskFor_IsNeitherResolvedNorBuilt()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem");

        var (search, documents) = await CreateFor(OneKindYaml, includeSearch: false, includeDocuments: true, null, files);

        Assert.Null(search);
        Assert.Same(files.Documents, documents);
        Assert.Equal(0, files.SearchBuilds);
        Assert.Equal(1, files.DocumentBuilds);
    }

    [Fact]
    public async Task AKindNoAdapterServes_FailsNothingWhenTheCallerDidNotAskForThatPort()
    {
        // The document reads from 'filesystem' and this host registers only the ranker. The caller
        // holds the document port itself, so the field that names 'filesystem' is never looked up.
        FakeKnowledgeStoreAdapter zilliz = new("zilliz") { CanServeDocuments = false, ReadsWhatItRanks = false };

        var (search, documents) = await CreateFor(TwoKindsYaml, includeSearch: true, includeDocuments: false, null, zilliz);

        Assert.Same(zilliz.Search, search);
        Assert.Null(documents);
        Assert.Equal(0, zilliz.DocumentBuilds);
    }

    [Fact]
    public async Task ACallerThatHoldsBothPorts_BuildsNothingAtAll()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem");

        var (search, documents) = await CreateFor(TwoKindsYaml, includeSearch: false, includeDocuments: false, null, files);

        // Not one field is read, so a document this host cannot serve does not stop the start.
        Assert.Null(search);
        Assert.Null(documents);
        Assert.Equal(0, files.SearchBuilds);
        Assert.Equal(0, files.DocumentBuilds);
    }

    // ---------------------------------------------------------------------------------------------
    // What it refuses, at startup and never on the first call.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ASearchKindNoAdapterServes_FailsAndNamesTheRegisteredKinds()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(TwoKindsYaml, new FakeKnowledgeStoreAdapter("filesystem")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/search", error.Pointer);
        Assert.Contains("zilliz", error.Message, StringComparison.Ordinal);
        Assert.Contains("'filesystem'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentKindNoAdapterServes_FailsAndNamesTheRegisteredKinds()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(TwoKindsYaml, new FakeKnowledgeStoreAdapter("zilliz")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/documents", error.Pointer);
        Assert.Contains("filesystem", error.Message, StringComparison.Ordinal);
        Assert.Contains("'zilliz'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdapterThatDoesNotServeThatPort_FailsAndNamesThePort()
    {
        // The Zilliz connector ranks and reads nothing, so a document that reads from it is a fault.
        FakeKnowledgeStoreAdapter zilliz = new("zilliz") { CanServeDocuments = false };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(BothPortsOn("zilliz"), zilliz));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/documents", error.Pointer);
        Assert.Contains("'zilliz'", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IDocumentStorePort), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdapterThatDoesNotRank_FailsTheSearchPort()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem") { CanServeSearch = false };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(OneKindYaml, files));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/search", error.Pointer);
        Assert.Contains(nameof(IKnowledgeRetrievalPort), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoAdaptersOfOneKind_FailTheBuild()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () => await Create(
            OneKindYaml,
            new FakeKnowledgeStoreAdapter("filesystem"),
            new FakeKnowledgeStoreAdapter("FileSystem")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/search", error.Pointer);
        Assert.Contains("filesystem", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostThatRegistersNoAdapter_FailsAndSaysSo()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(OneKindYaml));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/knowledge/search", error.Pointer);
        Assert.Contains("no adapter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsBuilt_UntilBothPortsFoundTheirAdapter()
    {
        FakeKnowledgeStoreAdapter files = new("filesystem");

        await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(TwoKindsYaml, files));

        // The document names 'zilliz' for search and no adapter serves it, so the reading half is
        // never opened either. A failed start opens no store at all.
        Assert.Equal(0, files.DocumentBuilds);
        Assert.Equal(0, files.SearchBuilds);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    /// <summary>Writes a document that binds both ports to one kind.</summary>
    private static string BothPortsOn(string kind)
        => $$"""
            apiVersion: agentcore/v1
            name: one-kind
            agents:
              items:
                - { id: only, instructions: "I answer everything" }
            providers:
              knowledge:
                search: {{kind}}
                documents: {{kind}}
            """;

    private static ValueTask<(IKnowledgeRetrievalPort? Search, IDocumentStorePort? Documents)> Create(
        string yaml,
        params IKnowledgeStoreAdapter[] adapters)
        => Create(yaml, null, adapters);

    private static ValueTask<(IKnowledgeRetrievalPort? Search, IDocumentStorePort? Documents)> Create(
        string yaml,
        ISecretResolverPort? secrets,
        params IKnowledgeStoreAdapter[] adapters)
        => CreateFor(yaml, includeSearch: true, includeDocuments: true, secrets, adapters);

    private static ValueTask<(IKnowledgeRetrievalPort? Search, IDocumentStorePort? Documents)> CreateFor(
        string yaml,
        bool includeSearch,
        bool includeDocuments,
        ISecretResolverPort? secrets,
        params IKnowledgeStoreAdapter[] adapters)
        => CompositeKnowledgeStoreFactory.CreateAsync(
            ConfigurationLoader.LoadYaml(yaml),
            secrets,
            adapters,
            includeSearch,
            includeDocuments,
            TestContext.Current.CancellationToken);
}

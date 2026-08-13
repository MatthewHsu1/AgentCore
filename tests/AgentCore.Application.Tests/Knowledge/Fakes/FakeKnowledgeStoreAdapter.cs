using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Tests.Knowledge.Fakes;

/// <summary>
/// An offline knowledge vendor. It answers one <c>kind</c> and records what it was asked to build.
/// </summary>
/// <remarks>
/// A vendor decides which ports it serves, so this fake takes both answers from its constructor.
/// <see cref="ReadsWhatItRanks"/> says whether the object <see cref="CreateSearchAsync"/> returns
/// also reads documents, which is the one condition the composite memoizes on.
/// </remarks>
internal sealed class FakeKnowledgeStoreAdapter : IKnowledgeStoreAdapter
{
    public FakeKnowledgeStoreAdapter(string kind)
        => Kind = kind;

    public string Kind { get; }

    public bool CanServeSearch { get; init; } = true;

    public bool CanServeDocuments { get; init; } = true;

    /// <summary>Gets whether the ranked object reads documents too, as the file store does.</summary>
    public bool ReadsWhatItRanks { get; init; } = true;

    /// <summary>Gets how many times the composite asked this adapter to rank.</summary>
    public int SearchBuilds { get; private set; }

    /// <summary>Gets how many times the composite asked this adapter to read.</summary>
    public int DocumentBuilds { get; private set; }

    /// <summary>Gets the entry the last build received.</summary>
    public KnowledgeProviderConfiguration? LastEntry { get; private set; }

    /// <summary>Gets the resolver chain the last build received.</summary>
    public ISecretResolverPort? LastSecrets { get; private set; }

    /// <summary>Gets the object the last search build returned.</summary>
    public IKnowledgeRetrievalPort? Search { get; private set; }

    /// <summary>Gets the object the last document build returned.</summary>
    public IDocumentStorePort? Documents { get; private set; }

    public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!CanServeSearch)
        {
            throw new NotSupportedException($"the '{Kind}' adapter does not rank.");
        }

        SearchBuilds++;
        LastEntry = entry;
        LastSecrets = secrets;
        Search = ReadsWhatItRanks ? new FakeKnowledgeStore() : new FakeSearchOnlyStore();
        return ValueTask.FromResult(Search);
    }

    public ValueTask<IDocumentStorePort> CreateDocumentsAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!CanServeDocuments)
        {
            throw new NotSupportedException($"the '{Kind}' adapter does not read.");
        }

        DocumentBuilds++;
        LastEntry = entry;
        LastSecrets = secrets;
        Documents = new FakeKnowledgeStore();
        return ValueTask.FromResult(Documents);
    }
}

/// <summary>A store that answers both knowledge ports, exactly as the file store does.</summary>
internal sealed class FakeKnowledgeStore : IKnowledgeRetrievalPort, IDocumentStorePort
{
    public ValueTask<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<KnowledgeChunk>>([]);

    public ValueTask<KnowledgeDocument?> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<KnowledgeDocument?>(null);

    public ValueTask<DocumentListing> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new DocumentListing { DocumentIds = [], Truncated = false });

    public ValueTask<GrepResult> GrepAsync(
        string pattern,
        string? glob = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new GrepResult { Matches = [], Truncated = false });
}

/// <summary>A store that ranks and reads nothing, as a vector store does.</summary>
internal sealed class FakeSearchOnlyStore : IKnowledgeRetrievalPort
{
    public ValueTask<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<KnowledgeChunk>>([]);
}

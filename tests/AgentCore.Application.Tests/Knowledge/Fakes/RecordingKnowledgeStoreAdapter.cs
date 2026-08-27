using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Knowledge.Fakes;

/// <summary>
/// An offline knowledge vendor. It answers one <c>kind</c> and records what it was asked to build.
/// </summary>
internal sealed class RecordingKnowledgeStoreAdapter : IKnowledgeStoreAdapter
{
    public RecordingKnowledgeStoreAdapter(string kind)
        => Kind = kind;

    public string Kind { get; }

    public bool CanServeSearch { get; init; } = true;

    public bool CanScope { get; init; } = true;

    /// <summary>Gets whether the composite asked this adapter to build.</summary>
    public bool CreateSearchCalled { get; private set; }

    /// <summary>Gets the entry the last build received.</summary>
    public KnowledgeProviderConfiguration? LastEntry { get; private set; }

    /// <summary>Gets the resolver chain the last build received.</summary>
    public ISecretResolverPort? LastSecrets { get; private set; }

    /// <summary>Gets <c>requireScope</c> the last build received.</summary>
    public bool LastRequireScope { get; private set; }

    /// <summary>Gets the embedding generator the last build received.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? LastEmbeddings { get; private set; }

    /// <summary>Gets the object the last build returned.</summary>
    public IKnowledgeRetrievalPort? Search { get; private set; }

    public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        bool requireScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!CanServeSearch)
        {
            throw new NotSupportedException($"the '{Kind}' adapter does not rank.");
        }

        CreateSearchCalled = true;
        LastEntry = entry;
        LastSecrets = secrets;
        LastEmbeddings = embeddings;
        LastRequireScope = requireScope;
        Search = new FakeKnowledgeStore();
        return ValueTask.FromResult(Search);
    }
}

/// <summary>A store that answers the knowledge port with nothing.</summary>
internal sealed class FakeKnowledgeStore : IKnowledgeRetrievalPort
{
    public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using Qdrant.Client;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>
/// The <c>qdrant</c> knowledge vendor: a read-only Qdrant collection behind the ranking port.
/// </summary>
public sealed class QdrantKnowledgeAdapter : IKnowledgeStoreAdapter
{
    /// <summary>The one <c>kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "qdrant";

    /// <summary>The <c>${secret:name}</c> name the resolver chain is asked for.</summary>
    public const string ApiKeySecretName = KnownSecrets.QdrantApiKeyName;

    /// <summary>The standard Qdrant environment variable, read when the chain holds no name.</summary>
    public const string ApiKeyVariableName = KnownSecrets.QdrantApiKeyVariable;

    // The JSON Pointer a missing or unreadable cluster URL reports.
    private const string EndpointPointer = "/providers/knowledge/endpoint";

    // The JSON Pointer a missing embedding generator reports.
    private const string EmbeddingsPointer = "/providers/embeddings";

    // The deadline of one gRPC call. The connector never sets one on its own.
    private static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(30);

    private readonly Func<KnowledgeProviderConfiguration, QdrantClient>? _clientFactory;

    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;

    private IReadOnlyList<IKnowledgeQueryAnalyzer> _analyzers = [new NoQueryAnalyzer()];

    private IKnowledgePointMapper[] _mappers = [];

    /// <summary>
    /// Creates the adapter that embeds through the generator <c>providers.embeddings</c> builds.
    /// </summary>
    public QdrantKnowledgeAdapter()
    {
    }

    /// <summary>Creates the adapter over a generator the caller builds.</summary>
    public QdrantKnowledgeAdapter(IEmbeddingGenerator<string, Embedding<float>> embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        _embeddings = embeddings;
    }

    /// <summary>Creates the adapter over a client the caller builds.</summary>
    internal QdrantKnowledgeAdapter(
        Func<KnowledgeProviderConfiguration, QdrantClient> clientFactory,
        IEmbeddingGenerator<string, Embedding<float>> embeddings)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(embeddings);

        _clientFactory = clientFactory;
        _embeddings = embeddings;
    }

    /// <summary>Creates the adapter over a client the caller builds, embedding through the port.</summary>
    internal QdrantKnowledgeAdapter(Func<KnowledgeProviderConfiguration, QdrantClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);

        _clientFactory = clientFactory;
    }

    /// <summary>Gets the one <c>kind</c> value this adapter serves.</summary>
    public string Kind => ProviderKind;

    /// <summary>Gets <see langword="true"/>: a Qdrant collection is what ranks.</summary>
    public bool CanServeSearch => true;

    /// <summary>Gets <see langword="true"/>: a facet filter narrows the query before it ranks.</summary>
    public bool CanScope => true;

    /// <summary>Replaces the analyzers <c>providers.knowledge.analyzer</c> may name.</summary>
    public QdrantKnowledgeAdapter UseAnalyzers(params IKnowledgeQueryAnalyzer[] analyzers)
    {
        ArgumentNullException.ThrowIfNull(analyzers);

        _analyzers = analyzers;
        return this;
    }

    /// <summary>Registers the mappers <c>providers.knowledge.mapper</c> may name.</summary>
    public QdrantKnowledgeAdapter UseMappers(params IKnowledgePointMapper[] mappers)
    {
        ArgumentNullException.ThrowIfNull(mappers);

        _mappers = mappers;
        return this;
    }

    /// <inheritdoc />
    public async ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        bool requireScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var embedder = _embeddings ?? embeddings ?? throw Fail(
            EmbeddingsPointer,
            "providers.knowledge is kind: " + ProviderKind + ", which ranks by vector and needs an "
            + "embedding generator. Write a providers.embeddings block, such as "
            + "{ kind: openai, model: text-embedding-3-small }, or construct QdrantKnowledgeAdapter "
            + "with a generator.");

        var analyzer = ResolveAnalyzer(entry.Analyzer);
        var mapper = ResolveMapper(entry.Mapper);

        AssertFieldsMapped(entry);
        var linkNamespace = LinkNamespace(entry);

        var client = _clientFactory is not null
            ? _clientFactory(entry)
            : await BuildClientAsync(entry, secrets, cancellationToken).ConfigureAwait(false);

        try
        {
            await QdrantCollectionProof
                .AssertAsync(client, entry, embedder, mapper, linkNamespace, cancellationToken)
                .ConfigureAwait(false);

            return new QdrantKnowledgeStore(
                new QdrantSearchChannel(client),
                embedder,
                new QdrantKnowledgeStoreOptions
                {
                    Collection = entry.Collection,
                    Scoped = requireScope,
                    VectorName = entry.Vector,
                    Fields = entry.Fields,
                    ScopeTemplate = entry.Scope.Template,
                    ScopeWildcard = entry.Scope.Wildcard?.Value,
                    ScopeWildcardFacets = entry.Scope.Wildcard?.Facets ?? [],
                    Links = entry.Links,
                    LinkNamespace = linkNamespace,
                    Analyzer = analyzer,
                    Mapper = mapper,
                    ScoreFloor = entry.ScoreFloor,
                    Limit = AgentKnowledgeConfiguration.MaximumLimit,
                });
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Refuses a document whose <c>fields:</c> block cannot feed what the rest of it asks for.</summary>
    private static void AssertFieldsMapped(KnowledgeProviderConfiguration entry)
    {
        if (entry.Mapper is null && entry.Fields?.Body is not { Length: > 0 })
        {
            throw Fail(
                "/providers/knowledge/fields/body",
                "providers.knowledge names no mapper, so the built-in fields: mapping reads every "
                + "card, and it maps no body. AgentCore has no default field names, so every card "
                + "would reach the model empty. Map providers.knowledge.fields.body, or name an "
                + "IKnowledgePointMapper with mapper:.");
        }

        if (entry.Links is not null && entry.Fields?.Id is not { Length: > 0 })
        {
            throw Fail(
                "/providers/knowledge/fields/id",
                "providers.knowledge.links is configured, and every links.lookup mode resolves a "
                + "linked id through providers.knowledge.fields.id, which this document does not map. "
                + "Map the id field, or remove the links block.");
        }

        if (entry.Links is { } declaredLinks && declaredLinks.Field is not { Length: > 0 })
        {
            throw Fail(
                "/providers/knowledge/links/field",
                "providers.knowledge.links is configured and names no field. AgentCore has no default "
                + "link field: it cannot guess which payload key holds this collection's outbound ids. "
                + "Write links.field, or remove the links block.");
        }
    }

    /// <summary>The UUID5 namespace the links block names, or <see cref="Guid.Empty"/> when links do not use one.</summary>
    private static Guid LinkNamespace(KnowledgeProviderConfiguration entry)
    {
        if (entry.Links is not { Lookup: KnowledgeLinkLookup.Uuid5 } uuid5Links)
        {
            return Guid.Empty;
        }

        try
        {
            return Uuid5PointId.Namespace(uuid5Links.Namespace);
        }
        catch (FormatException)
        {
            throw Fail(
                "/providers/knowledge/links/namespace",
                $"providers.knowledge.links.namespace is '{uuid5Links.Namespace}', which is neither a "
                + "known name (url, dns, oid, x500) nor a GUID.");
        }
    }

    /// <summary>Picks the analyzer the document named.</summary>
    /// <exception cref="ConfigurationLoadException">No registered analyzer answers to that name.</exception>
    private IKnowledgeQueryAnalyzer ResolveAnalyzer(string name)
        => _analyzers.FirstOrDefault(analyzer => string.Equals(analyzer.Name, name, StringComparison.Ordinal))
            ?? throw Fail(
                "/providers/knowledge/analyzer",
                $"providers.knowledge.analyzer is '{name}', and no registered IKnowledgeQueryAnalyzer "
                + $"answers to it. This host registers {string.Join(", ", _analyzers.Select(a => $"'{a.Name}'"))}. "
                + "Register one with QdrantKnowledgeAdapter.UseAnalyzers, or name one of those.");

    /// <summary>Picks the mapper the document named, or <see langword="null"/> for the built-in <c>fields:</c> mapping.</summary>
    /// <exception cref="ConfigurationLoadException">No registered mapper answers to that name.</exception>
    private IKnowledgePointMapper? ResolveMapper(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        return _mappers.FirstOrDefault(mapper => string.Equals(mapper.Name, name, StringComparison.Ordinal))
            ?? throw Fail(
                "/providers/knowledge/mapper",
                $"providers.knowledge.mapper is '{name}', and no registered IKnowledgePointMapper "
                + $"answers to it. This host registers "
                + $"{(_mappers.Length == 0 ? "none" : string.Join(", ", _mappers.Select(m => $"'{m.Name}'")))}. "
                + "Register one with QdrantKnowledgeAdapter.UseMappers, or drop the setting.");
    }

    /// <summary>Parses the endpoint and resolves the API key, then builds the production client.</summary>
    private static async ValueTask<QdrantClient> BuildClientAsync(
        KnowledgeProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint(entry);
        var apiKey = await secrets.TryReadAsync(KnownSecrets.Qdrant, cancellationToken).ConfigureAwait(false);

        return new QdrantClient(endpoint, apiKey, grpcTimeout: CallDeadline);
    }

    /// <summary>Reads the cluster URL out of the document.</summary>
    private static Uri Endpoint(KnowledgeProviderConfiguration entry)
    {
        if (entry.Endpoint is not { Length: > 0 } endpoint || string.IsNullOrWhiteSpace(endpoint))
        {
            throw Fail(
                EndpointPointer,
                "providers.knowledge is kind: " + ProviderKind + ", and that store needs "
                + "providers.knowledge.endpoint. Write the Qdrant cluster URL there, such as "
                + "https://qdrant.example.com:6334.");
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var url)
            ? url
            : throw Fail(
                EndpointPointer,
                "providers.knowledge.endpoint is '" + endpoint + "', which is not an absolute URL. Write "
                + "the Qdrant cluster URL, such as https://qdrant.example.com:6334.");
    }

    /// <summary>Builds the one exception every configuration failure of this adapter uses.</summary>
    private static ConfigurationLoadException Fail(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });
}
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Google.Protobuf.Collections;
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;

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

    // Embedded once at startup, only to measure the deployment's embedder width against the
    // collection's own. Its content is never a real query and is never sent to Qdrant.
    private const string DimensionProbeText = "agentcore-qdrant-startup-probe";

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

        var linkNamespace = Guid.Empty;

        if (entry.Links is { Lookup: KnowledgeLinkLookup.Uuid5 } uuid5Links)
        {
            try
            {
                linkNamespace = Uuid5PointId.Namespace(uuid5Links.Namespace);
            }
            catch (FormatException)
            {
                throw Fail(
                    "/providers/knowledge/links/namespace",
                    $"providers.knowledge.links.namespace is '{uuid5Links.Namespace}', which is neither a "
                    + "known name (url, dns, oid, x500) nor a GUID.");
            }
        }

        var client = _clientFactory is not null
            ? _clientFactory(entry)
            : await BuildClientAsync(entry, secrets, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!await client.CollectionExistsAsync(entry.Collection, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"providers.knowledge.collection names '{entry.Collection}', and no such collection or "
                    + "alias exists on this cluster. AgentCore reads a knowledge base and never creates "
                    + "one: run whatever ingests your cards, or correct the name.");
            }

            var info = await client.GetCollectionInfoAsync(entry.Collection, cancellationToken).ConfigureAwait(false);

            var vectors = info.Config.Params.VectorsConfig;

            ulong width;

            if (entry.Vector is { Length: > 0 } vectorName)
            {
                if (vectors.ConfigCase != VectorsConfig.ConfigOneofCase.ParamsMap
                    || !vectors.ParamsMap.Map.TryGetValue(vectorName, out var dense))
                {
                    throw new InvalidOperationException(
                        $"'{entry.Collection}' carries no named vector '{vectorName}'. A collection whose "
                        + "vector is unnamed, or named something else, misses every point on search with no "
                        + "error — and it still fetches by key, so a smoke test would not notice. Set "
                        + "providers.knowledge.vector to the name the collection was built with, or drop "
                        + "the setting for a collection with a single anonymous vector.");
                }

                width = dense.Size;
            }
            else
            {
                if (vectors.ConfigCase != VectorsConfig.ConfigOneofCase.Params)
                {
                    throw new InvalidOperationException(
                        $"'{entry.Collection}' carries named vectors and providers.knowledge.vector names "
                        + "none, so every query would search an anonymous vector this collection does not "
                        + "have and miss every point with no error. Set providers.knowledge.vector to the "
                        + "name the collection was built with.");
                }

                width = vectors.Params.Size;
            }

            ulong dimensions;

            try
            {
                var probe = await embedder
                    .GenerateAsync(DimensionProbeText, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                dimensions = (ulong)probe.Vector.Length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"'{entry.Collection}' could not be checked against this host's embedder: the startup "
                    + "width probe failed before it produced a vector. This is not a schema problem with "
                    + "the collection; check the embedding generator's own configuration and credentials.",
                    ex);
            }

            if (width != dimensions)
            {
                var label = entry.Vector is { Length: > 0 } named ? $"a '{named}' vector" : "an anonymous vector";
                throw new InvalidOperationException(
                    $"'{entry.Collection}' has {label} of {width} dimensions and this "
                    + $"host embeds at {dimensions}. Every score would be meaningless. Either set "
                    + "providers.embeddings to the model this collection was built with, or rebuild the "
                    + "collection with this host's model.");
            }

            await AssertPayloadAsync(client, entry, mapper, linkNamespace, cancellationToken).ConfigureAwait(false);

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
                    Links = entry.Links,
                    LinkNamespace = linkNamespace,
                    Analyzer = analyzer,
                    Mapper = mapper,
                    ScoreFloor = entry.ScoreFloor,

                    // Ruling 14(c). One store serves every agent and every agent reads from that one
                    // fetch, so the store must fetch what the MOST generous agent is allowed to ask
                    // for. Leaving the store's own default here silently caps an agent that wrote
                    // limit: 8 -- the schema accepts the 8, the provider trims to 8, and only 5 ever
                    // arrive. The document's ceiling is the store's floor.
                    Limit = AgentKnowledgeConfiguration.MaximumLimit,
                });
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads one point back and proves the payload still carries what the store reads off it.
    /// </summary>
    private static async ValueTask AssertPayloadAsync(
        QdrantClient client,
        KnowledgeProviderConfiguration entry,
        IKnowledgePointMapper? mapper,
        Guid linkNamespace,
        CancellationToken cancellationToken)
    {
        var scrolled = await client
            .ScrollAsync(entry.Collection, limit: 1, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (scrolled.Result.Count == 0)
        {
            return;
        }

        var point = scrolled.Result[0];

        if (mapper is not null)
        {
            // A custom mapper owns the payload shape, so no key list can be asserted — but the
            // refuse-to-start property survives: map the one scrolled point and demand text.
            var card = mapper.Map(QdrantPointConverter.ToPoint(point.Id, point.Payload, score: null));
            if (card is not { Text.Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"'{entry.Collection}' holds points that mapper '{mapper.Name}' reads as empty: it "
                    + "returned no card, or a card with no text, for a point scrolled at startup. "
                    + "AgentCore would then inject blank cards into every turn without failing anything. "
                    + "Correct the mapper, or point this host at the collection it was written for.");
            }

            return;
        }

        foreach (var (role, key, numeric) in DeclaredKeys(entry.Fields!))
        {
            if (!Carries(point.Payload, key, numeric))
            {
                throw new InvalidOperationException(
                    $"'{entry.Collection}' holds points whose payload has no {(numeric ? "numeric" : "non-empty")} "
                    + $"'{key}', which providers.knowledge.fields.{role} names. AgentCore reads every field "
                    + "that block maps off every point and treats a missing key as absent, so this role "
                    + "would be silently empty on every card. Map the path this collection really uses, "
                    + "declare the role with an explicit null if the collection does not carry it, or "
                    + "point this host at the collection whose payload matches.");
            }
        }

        if (entry.Links is { } links && links.Lookup != KnowledgeLinkLookup.Filter)
        {
            AssertPointKey(point, entry, links, linkNamespace);
        }
    }

    /// <summary>
    /// Every payload key the document actually mapped, with the role that named it and whether that
    /// role holds a number rather than text.
    /// </summary>
    /// <remarks>
    /// All six roles, not the two the built-in mapping cannot do without. A wrong path under
    /// <c>source</c>, <c>locator</c> or <c>authority</c> throws nothing and returns nothing: the
    /// citation is simply blank on every card, on every turn, for the life of the deployment. That is
    /// exactly the failure a startup proof exists to convert into a refusal to start.
    /// </remarks>
    private static IEnumerable<(string Role, string Key, bool Numeric)> DeclaredKeys(
        KnowledgeFieldsConfiguration fields)
    {
        if (fields.Body is { Length: > 0 } body)
        {
            yield return ("body", body, false);
        }

        if (fields.Id is { Length: > 0 } id)
        {
            yield return ("id", id, false);
        }

        if (fields.Lexical is { Length: > 0 } lexical)
        {
            yield return ("lexical", lexical, false);
        }

        if (fields.Source is { Length: > 0 } source)
        {
            yield return ("source", source, false);
        }

        if (fields.Locator is { Length: > 0 } locator)
        {
            yield return ("locator", locator, false);
        }

        // Authority ranks trust, so a collection writes it as an integer. Demanding a string here
        // would refuse every correctly built collection there is.
        if (fields.Authority is { Length: > 0 } authority)
        {
            yield return ("authority", authority, true);
        }
    }

    /// <summary>Whether one point really carries the role a mapped key claims.</summary>
    private static bool Carries(MapField<string, Value> payload, string key, bool numeric)
        => QdrantPayload.Read(payload, key) switch
        {
            { KindCase: Value.KindOneofCase.StringValue } value => !numeric && value.StringValue.Length > 0,
            { KindCase: Value.KindOneofCase.IntegerValue } => numeric,
            { KindCase: Value.KindOneofCase.DoubleValue } => numeric,
            _ => false,
        };

    /// <summary>Proves a linked id resolves back to the point that holds it.</summary>
    private static void AssertPointKey(
        RetrievedPoint point,
        KnowledgeProviderConfiguration entry,
        KnowledgeLinksConfiguration links,
        Guid linkNamespace)
    {
        if (QdrantPayload.Read(point.Payload, links.Field!) is null)
        {
            return;
        }

        var cardId = QdrantPayload.Read(point.Payload, entry.Fields!.Id!)!.StringValue;

        if (point.Id.PointIdOptionsCase != PointId.PointIdOptionsOneofCase.Uuid)
        {
            throw new InvalidOperationException(
                $"'{entry.Collection}' holds points keyed by number, and providers.knowledge.links.lookup "
                + $"is {links.Lookup.ToString().ToLowerInvariant()}, which builds a UUID key. Every "
                + "link expansion would silently return nothing. Set links.lookup: filter to match on "
                + $"'{entry.Fields!.Id}' instead.");
        }

        Guid expected;
        if (links.Lookup == KnowledgeLinkLookup.Direct)
        {
            if (!Guid.TryParse(cardId, out expected))
            {
                throw new InvalidOperationException(
                    $"'{entry.Collection}' holds a point whose '{entry.Fields.Id}' is '{cardId}', which is "
                    + "not a GUID, but providers.knowledge.links.lookup is direct. Qdrant's point key is a "
                    + "GUID or an unsigned integer, so a free-form id cannot be one. Set links.lookup: "
                    + $"filter to match on '{entry.Fields!.Id}' instead.");
            }
        }
        else
        {
            expected = Uuid5PointId.For(cardId, linkNamespace, links.Prefix);
        }

        if (Guid.Parse(point.Id.Uuid) != expected)
        {
            throw new InvalidOperationException(
                $"'{entry.Collection}' holds a point whose '{entry.Fields.Id}' is '{cardId}' and whose "
                + $"point key is {point.Id.Uuid}, but providers.knowledge.links.lookup is "
                + $"{links.Lookup.ToString().ToLowerInvariant()} with namespace "
                + $"'{links.Namespace}' and prefix '{links.Prefix}', which derives {expected}. "
                + $"Every '{links.Field}' expansion would silently return nothing. Correct the "
                + $"namespace or prefix, or set links.lookup: filter to match on '{entry.Fields!.Id}'.");
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
        => name is not { Length: > 0 }
            ? null
            : _mappers.FirstOrDefault(mapper => string.Equals(mapper.Name, name, StringComparison.Ordinal))
                ?? throw Fail(
                    "/providers/knowledge/mapper",
                    $"providers.knowledge.mapper is '{name}', and no registered IKnowledgePointMapper "
                    + $"answers to it. This host registers "
                    + $"{(_mappers.Length == 0 ? "none" : string.Join(", ", _mappers.Select(m => $"'{m.Name}'")))}. "
                    + "Register one with QdrantKnowledgeAdapter.UseMappers, or drop the setting.");

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

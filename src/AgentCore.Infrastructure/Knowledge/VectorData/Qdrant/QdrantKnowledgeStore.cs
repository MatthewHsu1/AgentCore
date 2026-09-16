using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Google.Protobuf.Collections;
using Microsoft.Extensions.AI;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>
/// The whole knowledge base over one Qdrant collection.
/// </summary>
internal sealed class QdrantKnowledgeStore
    : IKnowledgeRetrievalPort, IKnowledgeFacetReadPort, IDisposable
{
    private readonly IQdrantSearchChannel _channel;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly QdrantKnowledgeStoreOptions _options;
    private readonly IKnowledgePointMapper _mapper;
    private readonly QdrantScopeMatcher _scope;

    /// <summary>Binds one channel, one embedder and one collection.</summary>
    public QdrantKnowledgeStore(
        IQdrantSearchChannel channel,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        QdrantKnowledgeStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(options);

        _channel = channel;
        _embeddings = embeddings;
        _options = options;
        _scope = new QdrantScopeMatcher(options, ScopeTemplate.Parse(options.ScopeTemplate));
        // A links block with no field names no payload key to read outbound ids from. The adapter
        // rejects that in the document; this rejects it for a store built in code, where the
        // alternative is a null dereference on the first search that ranks anything.
        if (options.Links is { } links && links.Field is not { Length: > 0 })
        {
            throw new ArgumentException(
                "these options carry a links block that names no field, so there is no payload key to "
                + "read outbound ids from. AgentCore has no default link field; name one, or leave "
                + "Links null to turn link expansion off.",
                nameof(options));
        }

        // Every lookup mode resolves a linked id through the mapped id field: filter matches on it,
        // and uuid5 and direct derive the key from it. Unmapped, the filter path used to reach
        // protobuf with a null key and die there with nothing about the document in the message.
        if (options.Links is not null && options.Fields?.Id is not { Length: > 0 })
        {
            throw new ArgumentException(
                "these options carry a links block and map no fields.id. Every links.lookup mode "
                + "resolves a linked id through that field, so no link could ever be followed. Map "
                + "the id field, or leave Links null to turn link expansion off.",
                nameof(options));
        }

        _mapper = options.Mapper
            ?? (options.Fields is { Body.Length: > 0 } fields
                ? new FieldsPointMapper(fields)
                : throw new ArgumentException(
                    "these options name neither a mapper nor a fields.body, so no point could be read "
                    + "into a card and every turn would see an empty knowledge base. AgentCore has no "
                    + "default field names; set one of the two.",
                    nameof(options)));
    }

    /// <summary>
    /// Closes the channel, when it owns something closeable.
    /// </summary>
    public void Dispose() => (_channel as IDisposable)?.Dispose();

    /// <summary>Refuses an unscoped search when this deployment scopes every query.</summary>
    /// <param name="scope">The scope the search arrived with, or <see langword="null"/> for none.</param>
    /// <exception cref="InvalidOperationException">The search is unscoped, or its scope names no facets.</exception>
    private void RequireScope(KnowledgeScope? scope)
    {
        if (!_options.Scoped)
        {
            return;
        }

        // Two different host bugs, so two different messages. An empty facet map filters
        // nothing, which is the same leak as no scope at all: a host that reads a customer
        // record with no product on it builds one without noticing.
        if (scope is null)
        {
            throw new InvalidOperationException(
                "This deployment declares scoped: true and the search arrived without a KnowledgeScope. "
                + "An unscoped search serves every customer every card, so it fails instead. Pass the "
                + "scope to SearchAsync, or set scoped: false on the agent.");
        }

        if (scope.Facets.Count == 0)
        {
            throw new InvalidOperationException(
                "This deployment declares scoped: true and the open KnowledgeScope names no facets. "
                + "An empty facet map filters nothing, so the search would serve every customer every "
                + "card, and it fails instead. Give the scope the facets the customer record names, "
                + "or set scoped: false on the agent.");
        }
    }

    public async ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
        string query, KnowledgeScope? scope = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        RequireScope(scope);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Deadline);

        var embedding = await _embeddings
            .GenerateAsync(query, cancellationToken: deadline.Token).ConfigureAwait(false);

        var points = await _channel
            .QueryAsync(BuildQuery(query, embedding.Vector, scope), deadline.Token).ConfigureAwait(false);

        List<KnowledgeCard> cards = [];

        foreach (var point in points)
        {
            if (Map(point.Id, point.Payload, point.Score, viaLink: false) is { } card)
            {
                cards.Add(card);
            }
        }

        if (points.Count == 0 || _options.Links is not { } linksConfiguration)
        {
            return cards;
        }

        var have = cards.Select(card => card.CardId).ToHashSet(StringComparer.Ordinal);

        // The adapter refuses a links block that names no field, so this is never null here.
        var links = QdrantPayload.ReadList(points[0].Payload, linksConfiguration.Field!)
            .Where(id => !have.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (links.Count > 0)
        {
            var linked = await FetchLinkedAsync(linksConfiguration, links, deadline.Token).ConfigureAwait(false);

            foreach (var point in linked)
            {
                if (_scope.Matches(point.Payload, scope)
                    && Map(point.Id, point.Payload, score: null, viaLink: true) is { } card)
                {
                    cards.Add(card);
                }
            }
        }

        return cards;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The value is matched exactly, and never widened by <c>scope.wildcard</c> the way a scoped
    /// search widens one. A caller asking for the cards of one model year wants that year, not that
    /// year plus every card shared across all of them.
    /// </remarks>
    public async ValueTask<IReadOnlyList<KnowledgeCard>> ReadByFacetAsync(
        string path, string value, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var filter = new Filter();
        filter.Must.Add(new Condition
        {
            Field = new FieldCondition { Key = path, Match = new Match { Keyword = value } },
        });

        var points = await _channel
            .ScrollAsync(_options.Collection, filter, (uint)limit, cancellationToken)
            .ConfigureAwait(false);

        // A scroll carries no vector, so no score floor applies and every point that matched is a
        // card. Score stays null: nothing ranked these.
        List<KnowledgeCard> cards = [];
        foreach (var point in points)
        {
            if (Map(point.Id, point.Payload, score: null, viaLink: false) is { } card)
            {
                cards.Add(card);
            }
        }

        return cards;
    }

    private SearchQuery BuildQuery(string query, ReadOnlyMemory<float> vector, KnowledgeScope? scope)
    {
        var scopeFilter = new Filter();
        foreach (var (facet, value) in QdrantScopeMatcher.Facets(scope))
        {
            scopeFilter.Must.Add(new Condition
            {
                // Whatever scope.template produced, verbatim. A dotted path walks into a nested
                // struct; a flat key does not. Getting that wrong matches nothing at all, silently,
                // which is why the template is the deployment's to write and never AgentCore's to
                // guess.
                Field = new FieldCondition { Key = _scope.Path(facet), Match = _scope.MatchFor(facet, value) },
            });
        }

        // The prefetch depth scales with the limit. Hardcoding it caps the fused result far below
        // what the caller asked for, and the shortfall is silent.
        var depth = (ulong)Math.Max(_options.Limit * 2, 20);

        var prefetch = new List<PrefetchQuery> { Dense(vector, scopeFilter.Clone(), depth) };

        if (_options.Fields?.Lexical is { Length: > 0 } lexical)
        {
            var identifiers = _options.Analyzer.RequiredTerms(query);
            if (identifiers.Count > 0)
            {
                var identifierFilter = scopeFilter.Clone();
                foreach (var token in identifiers)
                {
                    // Must, never a nested Should. Under Should a card holding any one identifier is
                    // lifted in, which is the semantics measured to rank by storage order.
                    identifierFilter.Must.Add(new Condition
                    {
                        Field = new FieldCondition { Key = lexical, Match = new Match { Text = token } },
                    });
                }

                prefetch.Add(Dense(vector, identifierFilter, depth));
            }
        }

        // One leg has nothing to fuse, and fusing it anyway replaces every score with 1/(rank+1).
        // Re-scoring the prefetch with the same vector keeps a card's score a similarity, on the
        // scale the floor was written in.
        // Qdrant refuses "using" beside a fusion — the legs already name their vectors — so the
        // name rides the top level only when the top level is itself a nearest query.
        return prefetch.Count == 1
            ? new SearchQuery(
                _options.Collection,
                prefetch,
                new Query { Nearest = new VectorInput { Dense = new DenseVector { Data = { vector.ToArray() } } } },
                (ulong)_options.Limit,
                VectorName)
            : new SearchQuery(_options.Collection, prefetch, new Query { Fusion = Fusion.Rrf }, (ulong)_options.Limit);
    }

    private PrefetchQuery Dense(ReadOnlyMemory<float> vector, Filter filter, ulong depth)
    {
        var leg = new PrefetchQuery
        {
            Filter = filter,
            Limit = depth,
            Query = new Query
            {
                Nearest = new VectorInput { Dense = new DenseVector { Data = { vector.ToArray() } } },
            },
        };

        // The floor belongs here, on the leg, where the score is still a cosine similarity. Applying
        // it to what comes back is a different cut whenever the legs were fused: RRF replaces every
        // score with 1/(rank+1), so a floor on that number is a fixed rank cut whatever was asked.
        //
        // A floor of 0 means no floor: every cosine similarity is >= 0, so a threshold of 0 would
        // never cut anything, but it would still ride the wire and show up in a captured query.
        if (_options.ScoreFloor > 0)
        {
            leg.ScoreThreshold = (float)_options.ScoreFloor;
        }

        // "using" left unset queries the collection's anonymous vector; protobuf refuses null.
        if (VectorName is { } name)
        {
            leg.Using = name;
        }

        return leg;
    }

    /// <summary>
    /// The named vector every query scores with, or <see langword="null"/> for the collection's
    /// anonymous vector. An unset name and a blank one mean the same collection shape, and the
    /// prefetch leg and the top-level query have to agree on which.
    /// </summary>
    private string? VectorName => _options.VectorName is { Length: > 0 } name ? name : null;

    /// <summary>Fetches the cards a link named, under this deployment's lookup mode.</summary>
    private Task<IReadOnlyList<RetrievedPoint>> FetchLinkedAsync(
        KnowledgeLinksConfiguration links, List<string> ids, CancellationToken cancellationToken)
    {
        if (links.Lookup == KnowledgeLinkLookup.Filter)
        {
            var filter = new Filter();
            filter.Must.Add(new Condition
            {
                // The adapter refuses a links block without a mapped id, so this is never null here.
                Field = new FieldCondition
                {
                    Key = _options.Fields!.Id!,
                    Match = new Match { Keywords = new RepeatedStrings { Strings = { ids } } },
                },
            });

            return _channel.ScrollAsync(_options.Collection, filter, (uint)ids.Count, cancellationToken);
        }

        return _channel.RetrieveAsync(_options.Collection, [.. ids.Select(id => PointKey(links, id))], cancellationToken);
    }

    /// <summary>Turns one card id into the point key that holds it.</summary>
    private Guid PointKey(KnowledgeLinksConfiguration links, string cardId) => links.Lookup switch
    {
        KnowledgeLinkLookup.Direct => Guid.TryParse(cardId, out var id)
            ? id
            : throw new InvalidOperationException(
                $"links.lookup is direct and the card id '{cardId}' is not a GUID. Qdrant's point key "
                + "is a GUID or an unsigned integer, so a free-form id cannot be one. Use "
                + "links.lookup: filter to match on the id field instead."),
        _ => Uuid5PointId.For(cardId, _options.LinkNamespace, links.Prefix),
    };

    /// <summary>Maps one point, letting the mapper skip it, then stamps how it arrived.</summary>
    private KnowledgeCard? Map(PointId id, MapField<string, Value> payload, double? score, bool viaLink)
    {
        var card = _mapper.Map(QdrantPointConverter.ToPoint(id, payload, score));
        return card is null ? null : card with { Score = score, ViaLink = viaLink };
    }
}

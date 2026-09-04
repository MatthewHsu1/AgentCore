using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;

/// <summary>
/// A channel that answers scripted points and keeps every request the store built.
/// </summary>
/// <remarks>
/// The store's whole naming contract is visible in the requests it writes: which payload key a
/// facet became, which key the required-term leg matches on, which key a link filter reads. A live
/// server answers those requests but never reports them, so a test against Qdrant can only observe
/// the naming indirectly, through whether the right rows came back. This observes it directly, and
/// needs no server at all.
/// </remarks>
internal sealed class CapturingSearchChannel : IQdrantSearchChannel
{
    private readonly IReadOnlyList<ScoredPoint> _ranked;
    private readonly IReadOnlyList<RetrievedPoint> _fetched;

    /// <summary>Creates the channel.</summary>
    /// <param name="ranked">What <see cref="QueryAsync"/> answers.</param>
    /// <param name="fetched">What <see cref="ScrollAsync"/> and <see cref="RetrieveAsync"/> answer.</param>
    public CapturingSearchChannel(
        IReadOnlyList<ScoredPoint> ranked, IReadOnlyList<RetrievedPoint>? fetched = null)
    {
        _ranked = ranked;
        _fetched = fetched ?? [];
    }

    /// <summary>Gets the fused query the store built, or null when it never searched.</summary>
    public SearchQuery? Query { get; private set; }

    /// <summary>Gets the filter the store scrolled with, or null when it never scrolled.</summary>
    public Filter? ScrollFilter { get; private set; }

    /// <summary>Gets every id set the store fetched by key, in call order.</summary>
    public List<IReadOnlyList<Guid>> RetrievedIds { get; } = [];

    /// <summary>Gets every <c>Must</c> field condition of the first prefetch leg, keyed by payload path.</summary>
    public IReadOnlyDictionary<string, string> DenseFilterKeywords =>
        Query is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : Query.Prefetch[0].Filter.Must
                .Where(condition => condition.Field is { Match.Keyword.Length: > 0 })
                .ToDictionary(
                    condition => condition.Field.Key,
                    condition => condition.Field.Match.Keyword,
                    StringComparer.Ordinal);

    /// <summary>Gets the payload keys every required-term leg matches text on.</summary>
    public IReadOnlyList<string> LexicalKeys =>
        Query is null
            ? []
            : [.. Query.Prefetch
                .SelectMany(leg => leg.Filter.Must)
                .Where(condition => condition.Field is { Match.Text.Length: > 0 })
                .Select(condition => condition.Field.Key)];

    public Task<IReadOnlyList<ScoredPoint>> QueryAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        Query = query;
        return Task.FromResult(_ranked);
    }

    public Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(
        string collection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        RetrievedIds.Add(ids);
        return Task.FromResult(_fetched);
    }

    public Task<IReadOnlyList<RetrievedPoint>> ScrollAsync(
        string collection, Filter filter, uint limit, CancellationToken cancellationToken)
    {
        ScrollFilter = filter;
        return Task.FromResult(_fetched);
    }

    public Task<IReadOnlyList<string>> FacetAsync(
        string collection, string key, ulong limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("No test here drives a facet read; use QdrantSearchChannel against a live server.");
}

using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>One query, ready to send.</summary>
/// <param name="Collection">The collection, always an alias.</param>
/// <param name="Prefetch">The legs the top-level query draws its candidates from.</param>
/// <param name="Query">What ranks those candidates: a fusion over several legs, or a nearest re-score over one.</param>
/// <param name="Limit">How many results to return.</param>
/// <param name="Using">The named vector the top-level query scores with, or <see langword="null"/> for the collection's anonymous vector. Qdrant refuses it beside a fusion.</param>
internal sealed record SearchQuery(
    string Collection, IReadOnlyList<PrefetchQuery> Prefetch, Query Query, ulong Limit, string? Using = null);

/// <summary>
/// The seam between the store and the wire.
/// </summary>
internal interface IQdrantSearchChannel
{
    /// <summary>Sends one fused query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancels the call. The store links its deadline into this.</param>
    /// <returns>The scored points, best first.</returns>
    Task<IReadOnlyList<ScoredPoint>> QueryAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>Fetches whole points by key, in one call.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="ids">The keys.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The points that exist. An unknown key is left out; it does not throw.</returns>
    Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(
        string collection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Fetches whole points by a payload filter, in one call.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="filter">What the points must match.</param>
    /// <param name="limit">The most points to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The points that matched. An empty result is not an error.</returns>
    Task<IReadOnlyList<RetrievedPoint>> ScrollAsync(
        string collection, Filter filter, uint limit, CancellationToken cancellationToken);
}

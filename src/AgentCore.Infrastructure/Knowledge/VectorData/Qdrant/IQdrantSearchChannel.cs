using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>One fused query, ready to send.</summary>
/// <param name="Collection">The collection, always an alias.</param>
/// <param name="Prefetch">The prefetch legs the fusion combines.</param>
/// <param name="Query">The fusion itself.</param>
/// <param name="Limit">How many fused results to return.</param>
internal sealed record FusedQuery(
    string Collection, IReadOnlyList<PrefetchQuery> Prefetch, Query Query, ulong Limit);

/// <summary>
/// The seam between the store and the wire.
/// </summary>
internal interface IQdrantSearchChannel
{
    /// <summary>Sends one fused query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancels the call. The store links its deadline into this.</param>
    /// <returns>The scored points, best first.</returns>
    Task<IReadOnlyList<ScoredPoint>> QueryAsync(FusedQuery query, CancellationToken cancellationToken);

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

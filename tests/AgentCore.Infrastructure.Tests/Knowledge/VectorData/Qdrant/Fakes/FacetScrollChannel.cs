using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;

/// <summary>
/// A channel that answers one scripted <see cref="ScrollAsync"/> result and records the filter and
/// limit it was called with.
/// </summary>
/// <remarks>
/// Every other method throws. A facet read that ranks, retrieves by key or reads distinct values is
/// not a facet read, and the throw is what proves it: no embedding is generated and no vector is sent.
/// </remarks>
internal sealed class FacetScrollChannel(params RetrievedPoint[] points) : IQdrantSearchChannel
{
    private readonly IReadOnlyList<RetrievedPoint> _points = points;

    /// <summary>Gets the filter the last scroll carried, or <see langword="null"/> when none has run.</summary>
    public Filter? LastFilter { get; private set; }

    /// <summary>Gets the limit the last scroll carried.</summary>
    public uint LastLimit { get; private set; }

    public Task<IReadOnlyList<ScoredPoint>> QueryAsync(SearchQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException("a facet read never ranks.");

    public Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(
        string collection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        throw new NotSupportedException("a facet read never fetches by key.");

    public Task<IReadOnlyList<RetrievedPoint>> ScrollAsync(
        string collection, Filter filter, uint limit, CancellationToken cancellationToken)
    {
        LastFilter = filter;
        LastLimit = limit;
        return Task.FromResult(_points);
    }

    public Task<IReadOnlyList<string>> FacetAsync(
        string collection, string key, ulong limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("a facet read never reads distinct values.");
}

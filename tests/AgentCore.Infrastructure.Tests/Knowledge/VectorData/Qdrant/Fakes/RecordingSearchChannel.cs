using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;

/// <summary>
/// A channel that answers one scripted <see cref="QueryAsync"/> result and records every id
/// <see cref="RetrieveAsync"/> was asked to fetch by key.
/// </summary>
/// <remarks>
/// Exists for <c>links.lookup: direct</c>, which no live-server test in this repository exercises:
/// the synthetic and foreign corpora both key their points randomly, so only <c>filter</c> and
/// <c>uuid5</c> ever run against a real collection. <c>direct</c> needs nothing a live server would
/// add — the branch under test is <see cref="QdrantKnowledgeStore"/>'s own id handling, not Qdrant's.
/// </remarks>
internal sealed class RecordingSearchChannel : IQdrantSearchChannel
{
    private readonly IReadOnlyList<ScoredPoint> _queryResult;

    public RecordingSearchChannel(IReadOnlyList<ScoredPoint> queryResult) => _queryResult = queryResult;

    /// <summary>Gets every id set <see cref="RetrieveAsync"/> was called with, in call order.</summary>
    public List<IReadOnlyList<Guid>> RetrievedIds { get; } = [];

    public Task<IReadOnlyList<ScoredPoint>> QueryAsync(SearchQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(_queryResult);

    public Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(
        string collection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        RetrievedIds.Add(ids);
        return Task.FromResult<IReadOnlyList<RetrievedPoint>>([]);
    }

    public Task<IReadOnlyList<RetrievedPoint>> ScrollAsync(
        string collection, Filter filter, uint limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("These tests drive links.lookup: direct, which never scrolls.");
}

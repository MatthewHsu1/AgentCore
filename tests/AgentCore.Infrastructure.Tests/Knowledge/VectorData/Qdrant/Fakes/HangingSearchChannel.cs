using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;

/// <summary>A channel that never answers in time, for the two tests a live server cannot serve.</summary>
internal sealed class HangingSearchChannel : IQdrantSearchChannel
{
    private readonly TimeSpan _delay;

    public HangingSearchChannel(TimeSpan delay) => _delay = delay;

    public async Task<IReadOnlyList<ScoredPoint>> QueryAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        return [];
    }

    public async Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(
        string collection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        return [];
    }

    public async Task<IReadOnlyList<RetrievedPoint>> ScrollAsync(
        string collection, Filter filter, uint limit, CancellationToken cancellationToken)
    {
        await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        return [];
    }

    public async Task<IReadOnlyList<string>> FacetAsync(
        string collection, string key, ulong limit, CancellationToken cancellationToken)
    {
        await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        return [];
    }
}

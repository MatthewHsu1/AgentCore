using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>The real channel, over one <see cref="QdrantClient"/>.</summary>
internal sealed class QdrantSearchChannel : IQdrantSearchChannel, IDisposable
{
    private readonly QdrantClient _client;

    /// <summary>Binds one client.</summary>
    public QdrantSearchChannel(QdrantClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
    }

    /// <summary>Closes the client.</summary>
    public void Dispose() => _client.Dispose();

    /// <inheritdoc />
    public Task<IReadOnlyList<ScoredPoint>> QueryAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return RunAsync(
            () => _client.QueryAsync(
                query.Collection,
                prefetch: query.Prefetch,
                query: query.Query,
                usingVector: query.Using,
                limit: query.Limit,
                payloadSelector: new WithPayloadSelector { Enable = true },
                cancellationToken: cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(
        string collection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return RunAsync(
            () => _client.RetrieveAsync(
                collection,
                [.. ids.Select(id => new PointId { Uuid = id.ToString() })],
                withPayload: true,
                withVectors: false,
                cancellationToken: cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedPoint>> ScrollAsync(
        string collection, Filter filter, uint limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var response = await RunAsync(
            () => _client.ScrollAsync(
                collection,
                filter: filter,
                limit: limit,
                payloadSelector: new WithPayloadSelector { Enable = true },
                vectorsSelector: new WithVectorsSelector { Enable = false },
                cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return response.Result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FacetAsync(
        string collection, string key, ulong limit, CancellationToken cancellationToken)
    {
        // exact: true always. Measured equal to exact: false on 2- and 8-segment collections
        // (P4); the guarantee is the caller's and the measured cost was 6 ms.
        var response = await RunAsync(
            () => _client.FacetAsync(collection, key, limit: limit, exact: true, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return [.. response.Hits.Select(hit => hit.Value.StringValue)];
    }

    /// <summary>
    /// Runs one gRPC call and converts a client-side cancellation into <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
    }
}

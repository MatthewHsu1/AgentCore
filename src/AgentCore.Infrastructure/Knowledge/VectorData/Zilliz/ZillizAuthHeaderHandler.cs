using System.Net.Http.Headers;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;

/// <summary>
/// Writes the Zilliz key onto every request of one collection.
/// </summary>
/// <remarks>
/// <para>
/// The key is bound once, where <see cref="ZillizKnowledgeAdapter"/> resolves it, and it is written
/// once, here. <see cref="ZillizCollection"/> therefore holds no credential at all: the class that
/// builds a body and reads an answer cannot put a key in a message or a log, because it has none.
/// </para>
/// <para>
/// This handler sits above the pooled handler the host owns, so a request keeps the connection
/// lifetime, the deadline, and the retry of that pipeline and gains the header of this vendor.
/// </para>
/// </remarks>
internal sealed class ZillizAuthHeaderHandler : DelegatingHandler
{
    /// <summary>The scheme the Milvus v2 REST API reads the key under.</summary>
    private const string Scheme = "Bearer";

    private readonly string _apiKey;

    /// <summary>Binds the key every request of this collection carries.</summary>
    /// <param name="apiKey">The Zilliz key.</param>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> is empty or blank.</exception>
    /// <remarks>
    /// A key that is nothing is refused where it is bound, which is while the host starts. The
    /// alternative is a first search that fails on the telephone.
    /// </remarks>
    public ZillizAuthHeaderHandler(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _apiKey = apiKey;
    }

    /// <summary>Sends one request with the key of this collection on it.</summary>
    /// <param name="request">The request. Its authorization header is set here.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The answer of the inner handler.</returns>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // This handler owns the header, so a caller cannot send the cluster the wrong key.
        request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, _apiKey);

        return base.SendAsync(request, cancellationToken);
    }
}

using System.Net.Http.Headers;

namespace AgentCore.Infrastructure.Evaluation.OpenAiModeration;

/// <summary>
/// Writes the OpenAI key onto every moderation request.
/// </summary>
/// <remarks>
/// <para>
/// The key is bound once, where <see cref="OpenAiModerationEvaluator"/> resolves it, and it is
/// written once, here. The evaluator therefore holds no credential at all: the class that builds a
/// body, reads an answer, and writes a diagnostic cannot put a key in a message or a log, because it
/// has none.
/// </para>
/// <para>
/// This handler sits above the pooled handler the host owns, so a request keeps the connection
/// lifetime, the deadline, and the retry of that pipeline and gains the header of this vendor.
/// </para>
/// <para>
/// It is a second copy of <c>ZillizAuthHeaderHandler</c>. The two differ only in what they document,
/// and merging them into one shared bearer handler would edit a shipped public surface for no change
/// in behaviour. That merge is its own item.
/// </para>
/// </remarks>
internal sealed class OpenAiModerationAuthHandler : DelegatingHandler
{
    /// <summary>The scheme the OpenAI REST API reads the key under.</summary>
    private const string Scheme = "Bearer";

    private readonly string _apiKey;

    /// <summary>Binds the key every moderation request carries.</summary>
    /// <param name="apiKey">The OpenAI key.</param>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> is empty or blank.</exception>
    /// <remarks>
    /// A key that is nothing is refused where it is bound, which is while the host starts. The
    /// alternative is a first turn whose content check silently answers nothing.
    /// </remarks>
    public OpenAiModerationAuthHandler(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _apiKey = apiKey;
    }

    /// <summary>Sends one request with the OpenAI key on it.</summary>
    /// <param name="request">The request. Its authorization header is set here.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The answer of the inner handler.</returns>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // This handler owns the header, so a caller cannot send OpenAI the wrong key.
        request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, _apiKey);

        return base.SendAsync(request, cancellationToken);
    }
}

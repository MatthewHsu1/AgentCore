using System.Net;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>
/// A handler that answers one scripted status for each attempt, so a retry test reaches no network.
/// </summary>
/// <remarks>
/// The last status of the script answers every attempt after it, so a script of one status answers
/// forever. <see cref="Attempts"/> counts what the pipeline above this handler actually sent.
/// </remarks>
internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode[] _script;

    public ScriptedHttpMessageHandler(params HttpStatusCode[] script) => _script = script;

    /// <summary>Gets the number of requests this handler saw.</summary>
    public int Attempts { get; private set; }

    /// <summary>Gets or sets the <c>Retry-After</c> header every answer carries, in seconds.</summary>
    public int? RetryAfterSeconds { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var status = _script[Math.Min(Attempts, _script.Length - 1)];
        Attempts++;

        HttpResponseMessage response = new(status);
        if (RetryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(seconds));
        }

        return Task.FromResult(response);
    }
}

using System.Net;

namespace AgentCore.Infrastructure.Tests.Tools;

/// <summary>
/// A handler that answers from a script, so the HTTP tool tests reach no network.
/// </summary>
/// <remarks>
/// The tool takes its <see cref="HttpClient"/> from its constructor for exactly this reason. It
/// never builds one, so a test decides what the endpoint answers.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

    /// <summary>Gets every request this handler saw, in call order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Builds a handler that answers one status and one body.</summary>
    public static StubHttpMessageHandler Answering(HttpStatusCode status, string body, string mediaType = "application/json")
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
        });

    /// <summary>Builds a handler that fails the way <see cref="HttpClient"/> fails on a timeout.</summary>
    public static StubHttpMessageHandler TimingOut()
        => new(_ => throw new TaskCanceledException("The request timed out.", new TimeoutException()));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(_answer(request));
    }
}

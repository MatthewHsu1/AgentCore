using System.Net;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// The one outbound HTTP pipeline every adapter of this host sends on.
/// </summary>
/// <remarks>
/// The pipeline owns what no vendor should repeat: the connection lifetime, the deadline, and the
/// retry. A vendor adapter owns its base address, its credential, and its wire format, and nothing
/// else.
/// </remarks>
public sealed class AgentCoreHttpClientsTests
{
    private const string Name = "agentcore.test";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Uri Endpoint => new("https://endpoint.test/answer", UriKind.Absolute);

    [Fact]
    public async Task ItRetriesAServerFailureAndAnswersFromTheRetry()
    {
        using ScriptedHttpMessageHandler endpoint = new(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        using AgentCoreHttpClients clients = new(endpoint);

        using var response = await clients.CreateClient(Name).GetAsync(Endpoint, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, endpoint.Attempts);
    }

    [Fact]
    public async Task ItRetriesARateLimitAnswer()
    {
        using ScriptedHttpMessageHandler endpoint = new(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using AgentCoreHttpClients clients = new(endpoint);

        using var response = await clients.CreateClient(Name).GetAsync(Endpoint, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, endpoint.Attempts);
    }

    /// <summary>
    /// A POST that fails is not retried, because a <c>kind: http</c> tool that POSTs may have already
    /// fired its side effect, and repeating the attempt would fire it again.
    /// </summary>
    [Fact]
    public async Task AFailedPostIsSentOnlyOnce()
    {
        using ScriptedHttpMessageHandler endpoint = new(HttpStatusCode.ServiceUnavailable);
        using AgentCoreHttpClients clients = new(endpoint);

        using var response = await clients.CreateClient(Name).PostAsync(Endpoint, content: null, Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, endpoint.Attempts);
    }

    /// <summary>A GET is idempotent, so a server failure still gets retried.</summary>
    [Fact]
    public async Task AFailedGetIsStillRetried()
    {
        using ScriptedHttpMessageHandler endpoint = new(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using AgentCoreHttpClients clients = new(endpoint);

        using var response = await clients.CreateClient(Name).GetAsync(Endpoint, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, endpoint.Attempts);
    }

    [Fact]
    public async Task ItSendsAnAnswerTheEndpointMeantStraightBack()
    {
        using ScriptedHttpMessageHandler endpoint = new(HttpStatusCode.BadRequest);
        using AgentCoreHttpClients clients = new(endpoint);

        using var response = await clients.CreateClient(Name).GetAsync(Endpoint, Token);

        // A refusal is the answer. Repeating it costs a turn and changes nothing.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, endpoint.Attempts);
    }

    [Fact]
    public void EveryClientCarriesTheDeadlineOfThisPipeline()
    {
        using ScriptedHttpMessageHandler endpoint = new(HttpStatusCode.OK);
        using AgentCoreHttpClients clients = new(endpoint);

        // Any name is served without being registered first, so a vendor that names a new client
        // must still get the deadline rather than whatever the factory would hand out.
        Assert.Equal(AgentCoreHttpClients.RequestDeadline, clients.CreateClient(Name).Timeout);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, AgentCoreHttpClients.RequestDeadline);
    }

    /// <summary>
    /// A rate limit that names its own wait wins over the backoff of this pipeline.
    /// </summary>
    /// <remarks>
    /// Guessing shorter than the endpoint asked for gets the next attempt refused as well, and
    /// guessing longer holds the call for no reason.
    /// </remarks>
    [Fact]
    public void ItWaitsTheRetryAfterAnEndpointAsksFor()
    {
        using HttpResponseMessage asking = new(HttpStatusCode.TooManyRequests);
        asking.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), AgentCoreHttpClients.RetryAfter(asking));
    }

    [Fact]
    public void ItReadsARetryAfterWrittenAsADate()
    {
        using HttpResponseMessage asking = new(HttpStatusCode.ServiceUnavailable);
        asking.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            DateTimeOffset.UtcNow.AddMinutes(1));

        var wait = AgentCoreHttpClients.RetryAfter(asking);

        Assert.NotNull(wait);
        Assert.InRange(wait.Value, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void AnAnswerThatAsksForNoWaitLeavesTheBackoffOfThisPipelineInPlace()
    {
        using HttpResponseMessage silent = new(HttpStatusCode.ServiceUnavailable);

        // Null is how a delay generator says "use the strategy delay".
        Assert.Null(AgentCoreHttpClients.RetryAfter(silent));
        Assert.Null(AgentCoreHttpClients.RetryAfter(null));
    }

    [Fact]
    public void ADateThatHasAlreadyPassedWaitsNothingExtra()
    {
        using HttpResponseMessage stale = new(HttpStatusCode.ServiceUnavailable);
        stale.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Null(AgentCoreHttpClients.RetryAfter(stale));
    }

    [Fact]
    public async Task TheHandlerTheHostOwnsIsNotDisposedWithThePipeline()
    {
        using ScriptedHttpMessageHandler endpoint = new(HttpStatusCode.OK);

        using (AgentCoreHttpClients clients = new(endpoint))
        {
            using var first = await clients.CreateClient(Name).GetAsync(Endpoint, Token);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // The pipeline is closed, and the handler the host passed in still answers.
        using HttpClient direct = new(endpoint, disposeHandler: false);
        using var second = await direct.GetAsync(Endpoint, Token);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, endpoint.Attempts);
    }
}

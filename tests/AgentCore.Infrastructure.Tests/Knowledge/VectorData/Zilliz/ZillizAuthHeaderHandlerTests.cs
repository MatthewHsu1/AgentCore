using System.Net;
using System.Net.Http.Headers;
using AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;
using AgentCore.Infrastructure.Tests.Tools;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Zilliz;

/// <summary>
/// The one place the Zilliz key is written onto a request.
/// </summary>
/// <remarks>
/// The connector above this handler ranks and reads JSON. It holds no key, so no class that parses
/// an answer can put one in a message or a log.
/// </remarks>
public sealed class ZillizAuthHeaderHandlerTests
{
    private const string ApiKey = "zilliz-test-not-a-real-key";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Uri Endpoint => new("https://in03-test.serverless.gcp-us-west1.cloud.zilliz.com/v2", UriKind.Absolute);

    [Fact]
    public async Task ItSendsTheKeyAsABearerTokenOnEveryRequest()
    {
        using var cluster = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using ZillizAuthHeaderHandler handler = new(ApiKey) { InnerHandler = cluster };
        using HttpClient client = new(handler, disposeHandler: false);

        using var first = await client.GetAsync(Endpoint, Token);
        using var second = await client.GetAsync(Endpoint, Token);

        Assert.Equal(2, cluster.Requests.Count);
        Assert.All(cluster.Requests, request => Assert.Equal(
            "Bearer " + ApiKey,
            request.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task ItWritesOverAnAuthorizationHeaderTheCallerAlreadySet()
    {
        using var cluster = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using ZillizAuthHeaderHandler handler = new(ApiKey) { InnerHandler = cluster };
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, Endpoint)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "a-key-from-somewhere-else") },
        };

        using var response = await client.SendAsync(request, Token);

        // One handler owns this header, so a caller cannot send the cluster the wrong key.
        Assert.Equal("Bearer " + ApiKey, Assert.Single(cluster.Requests).Headers.Authorization!.ToString());
    }

    [Fact]
    public void AKeyThatIsNothingIsRefusedWhereItIsBoundAndNotOnTheFirstCall()
    {
        Assert.Throws<ArgumentNullException>(() => new ZillizAuthHeaderHandler(null!));
        Assert.Throws<ArgumentException>(() => new ZillizAuthHeaderHandler("   "));
    }
}

using System.Net;
using System.Net.Http.Headers;
using AgentCore.Infrastructure.Evaluation.OpenAiModeration;
using AgentCore.Infrastructure.Tests.Tools;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Evaluation.OpenAiModeration;

/// <summary>
/// The one place the OpenAI key is written onto a moderation request.
/// </summary>
/// <remarks>
/// The evaluator above this handler builds a body and reads an answer. It holds no key, so no class
/// that parses an answer can put one in a message, a diagnostic, or a log.
/// </remarks>
public sealed class OpenAiModerationAuthHandlerTests
{
    private const string ApiKey = "sk-test-not-a-real-key";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Uri Endpoint => new("https://api.openai.com/v1/moderations", UriKind.Absolute);

    [Fact]
    public async Task ItSendsTheKeyAsABearerTokenOnEveryRequest()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using OpenAiModerationAuthHandler handler = new(ApiKey) { InnerHandler = endpoint };
        using HttpClient client = new(handler, disposeHandler: false);

        using var first = await client.GetAsync(Endpoint, Token);
        using var second = await client.GetAsync(Endpoint, Token);

        Assert.Equal(2, endpoint.Requests.Count);
        Assert.All(endpoint.Requests, request => Assert.Equal(
            "Bearer " + ApiKey,
            request.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task ItWritesOverAnAuthorizationHeaderTheCallerAlreadySet()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using OpenAiModerationAuthHandler handler = new(ApiKey) { InnerHandler = endpoint };
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, Endpoint)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "a-key-from-somewhere-else") },
        };

        using var response = await client.SendAsync(request, Token);

        // One handler owns this header, so a caller cannot send OpenAI the wrong key.
        Assert.Equal("Bearer " + ApiKey, Assert.Single(endpoint.Requests).Headers.Authorization!.ToString());
    }

    [Fact]
    public void AKeyThatIsNothingIsRefusedWhereItIsBoundAndNotOnTheFirstCall()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenAiModerationAuthHandler(null!));
        Assert.Throws<ArgumentException>(() => new OpenAiModerationAuthHandler("   "));
    }
}

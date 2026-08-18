using System.Net;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Tools;

/// <summary>
/// The second tool kind of section 8.1: <c>kind: http</c>, which makes one call.
/// </summary>
/// <remarks>
/// The worked example declares <c>lookup_order</c>. The URL holds a <c>{orderId}</c> placeholder the
/// arguments fill, and the header holds a <c>${secret:orders-api-key}</c> reference that startup
/// already resolved.
/// </remarks>
public sealed class HttpToolFactoryTests
{
    private const string SecretName = "orders-api-key";
    private const string SecretValue = "sk-live-0123456789";

    private static readonly ToolConfiguration LookupOrder = new()
    {
        Id = "lookup_order",
        Kind = ToolKind.Http,
        Description = "Read one order by its identifier.",
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}"""),
        Request = new HttpRequestConfiguration
        {
            Method = "GET",
            Url = "https://api.example.com/orders/{orderId}",
            Headers = new Dictionary<string, SecretTemplate>(StringComparer.Ordinal)
            {
                ["Authorization"] = SecretTemplate.Parse("Bearer ${secret:orders-api-key}"),
            },
        },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // What the model reads.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TheDeclaredSchema_ReachesTheModelUnchanged()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);

        var function = Assert.IsAssignableFrom<AIFunction>(Factory(client).Create(LookupOrder));

        Assert.Equal("lookup_order", function.Name);
        Assert.Equal("Read one order by its identifier.", function.Description);
        Assert.True(JsonNode.DeepEquals(LookupOrder.Parameters, JsonNode.Parse(function.JsonSchema.GetRawText())));
    }

    // ---------------------------------------------------------------------------------------------
    // The call.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ThePlaceholderTakesItsValueFromTheArguments()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, """{"status":"shipped"}""");
        using HttpClient client = new(handler);

        var result = await CallAsync(Factory(client).Create(LookupOrder), ("orderId", "A-42"));

        Assert.Equal(new Uri("https://api.example.com/orders/A-42"), Assert.Single(handler.Requests).RequestUri);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);

        // The state layer reads lookup_order.status out of this, so the body stays a node tree.
        Assert.Equal("shipped", Assert.IsType<JsonObject>(result)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task APlaceholderValueIsEscaped()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);

        await CallAsync(Factory(client).Create(LookupOrder), ("orderId", "a/../b c"));

        Assert.Equal(
            "https://api.example.com/orders/a%2F..%2Fb%20c",
            handler.Requests[0].RequestUri!.OriginalString);
    }

    [Fact]
    public async Task TheResolvedHeaderReachesTheRequest()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);

        await CallAsync(Factory(client).Create(LookupOrder), ("orderId", "A-42"));

        Assert.Equal(
            "Bearer " + SecretValue,
            Assert.Single(handler.Requests).Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task ABodyThatIsNotJson_ComesBackAsText()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "shipped", "text/plain");
        using HttpClient client = new(handler);

        var result = await CallAsync(Factory(client).Create(LookupOrder), ("orderId", "A-42"));

        // The framework carries every tool result as a JSON node, so a text body arrives as a string
        // value rather than as an object the model would have to unwrap.
        Assert.Equal("shipped", Assert.IsAssignableFrom<JsonNode>(result).GetValue<string>());
    }

    // ---------------------------------------------------------------------------------------------
    // Section 8.7: a tool returns an error result and does not throw.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AFiveHundred_BecomesAnErrorResult()
    {
        using var handler = StubHttpMessageHandler.Answering(
            HttpStatusCode.InternalServerError,
            """{"detail":"the order service is down"}""");
        using HttpClient client = new(handler);

        var result = await CallAsync(Factory(client).Create(LookupOrder), ("orderId", "A-42"));

        AssertError(result, "500");
    }

    [Fact]
    public async Task ATimeout_ThrowsSoTheFrameworkBudgetCountsIt()
    {
        using var handler = StubHttpMessageHandler.TimingOut();
        using HttpClient client = new(handler);

        // The endpoint did not answer at all. The model cannot fix a deadline by rewording the
        // arguments, so this propagates and MaximumConsecutiveErrorsPerRequest ends the turn on the
        // fallback line, per section 8.7 row six. It used to become a result the model retried
        // against a dead endpoint for the whole turn.
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CallAsync(Factory(client).Create(LookupOrder), ("orderId", "A-42")));

        Assert.IsType<TimeoutException>(thrown.InnerException);
    }

    [Fact]
    public async Task ATransportFailure_ThrowsSoTheFrameworkBudgetCountsIt()
    {
        using StubHttpMessageHandler handler = new(_ => throw new HttpRequestException("no such host"));
        using HttpClient client = new(handler);

        // The host is not resolvable. See ATimeout_ThrowsSoTheFrameworkBudgetCountsIt.
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => CallAsync(Factory(client).Create(LookupOrder), ("orderId", "A-42")));

        Assert.Equal("no such host", thrown.Message);
    }

    [Fact]
    public async Task AnEndpointThatRefusedTheRequest_StillBecomesAnErrorResult()
    {
        // The counterpart, and the half that must not regress: the endpoint ANSWERED. What it said is
        // a fact the model reads and works around, so it never spends the budget. HttpTool writes
        // this result itself and never reaches the classification at all.
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.NotFound, """{"detail":"no such order"}""");
        using HttpClient client = new(handler);

        var result = await CallAsync(Factory(client).Create(LookupOrder), ("orderId", "A-42"));

        AssertError(result, "404");
    }

    [Fact]
    public async Task AMissingPlaceholderArgument_BecomesAnErrorResultAndMakesNoCall()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);

        var result = await CallAsync(Factory(client).Create(LookupOrder));

        AssertError(result, "orderId");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NoErrorResult_EverHoldsTheHeaderValue()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.Unauthorized, """{"detail":"no"}""");
        using HttpClient client = new(handler);

        var result = await CallAsync(Factory(client).Create(LookupOrder), ("orderId", "A-42"));

        Assert.DoesNotContain(SecretValue, result!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerThatHangsUp_CancelsAndDoesNotBecomeAnErrorResult()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);
        var function = Assert.IsAssignableFrom<AIFunction>(Factory(client).Create(LookupOrder));

        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await function.InvokeAsync(Arguments(("orderId", "A-42")), source.Token));
    }

    // ---------------------------------------------------------------------------------------------
    // Failing at startup.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AHttpToolWithNoRequest_FailsAtStartup()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Factory(client).Create(new ToolConfiguration { Id = "broken", Kind = ToolKind.Http }));

        Assert.Contains("broken", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeaderReferenceStartupNeverResolved_FailsAtStartup()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);
        HttpToolFactory factory = new(client, ResolvedSecrets.Empty);

        // The header resolves once, when the document compiles. A tool that reached a call with an
        // unresolved reference would fail on the telephone instead.
        Assert.Throws<SecretResolutionException>(() => factory.Create(LookupOrder));
    }

    [Fact]
    public void TheHttpFactory_ServesNoOtherKind()
    {
        using var handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);

        Assert.Null(Factory(client).Create(new ToolConfiguration { Id = "i", Kind = ToolKind.Agent, Agent = "a" }));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    private static HttpToolFactory Factory(HttpClient client)
        => new(client, ResolvedSecrets.Create([new KeyValuePair<string, string>(SecretName, SecretValue)]));

    private static AIFunctionArguments Arguments(params (string Name, object? Value)[] arguments)
    {
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            values[argument.Name] = argument.Value;
        }

        return new AIFunctionArguments(values);
    }

    private static async Task<object?> CallAsync(AITool? tool, params (string Name, object? Value)[] arguments)
    {
        var function = Assert.IsAssignableFrom<AIFunction>(tool);
        return await function.InvokeAsync(Arguments(arguments), Token);
    }

    private static void AssertError(object? result, string fragment)
    {
        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Equal("lookup_order", error["tool"]!.GetValue<string>());
        Assert.Contains(fragment, error["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }
}

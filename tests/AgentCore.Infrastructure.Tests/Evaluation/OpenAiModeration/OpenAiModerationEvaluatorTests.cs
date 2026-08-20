using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Evaluation.OpenAiModeration;
using AgentCore.Infrastructure.Llm.OpenAI;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Evaluation.OpenAiModeration;

/// <summary>
/// The moderation half of the evaluation seam, over the OpenAI moderations route.
/// </summary>
/// <remarks>
/// <para>
/// Every test here reaches the network zero times. The evaluator takes its <see cref="HttpClient"/>
/// from an internal constructor for exactly this reason, and the tests that build it the way a host
/// does pass <see cref="StubHandlerFactory"/> and <see cref="MapSecretResolver"/>.
/// </para>
/// <para>
/// The rule the failure tests hold to is D9: a judge never blocks a turn. A 500, a 429, a timeout,
/// and a body that is not JSON all produce one inconclusive metric and no exception.
/// </para>
/// </remarks>
public sealed class OpenAiModerationEvaluatorTests
{
    private const string ApiKey = "sk-test-not-a-real-key";
    private const string Reply = "Turn the machine off at the wall.";

    private const string Clean =
        """
        {
          "id": "modr-test",
          "model": "omni-moderation-latest",
          "results": [
            {
              "flagged": false,
              "categories": { "harassment": false, "hate": false, "violence": false },
              "category_scores": { "harassment": 0.001, "hate": 0.002, "violence": 0.003 }
            }
          ]
        }
        """;

    private const string Flagged =
        """
        {
          "id": "modr-test",
          "model": "omni-moderation-latest",
          "results": [
            {
              "flagged": true,
              "categories": {
                "harassment": true,
                "harassment/threatening": true,
                "hate": false,
                "hate/threatening": false,
                "illicit": null,
                "illicit/violent": null,
                "self-harm": false,
                "self-harm/instructions": false,
                "self-harm/intent": false,
                "sexual": false,
                "sexual/minors": false,
                "violence": true,
                "violence/graphic": false
              },
              "category_scores": {
                "harassment": 0.5215635299682617,
                "harassment/threatening": 0.5694745779037476,
                "violence": 0.9971134662628174
              }
            }
          ]
        }
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheEvaluatorNamesOneMetric()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
        var evaluator = await BuildAsync(endpoint);

        Assert.Equal(["Content Safety"], evaluator.EvaluationMetricNames);
        Assert.Equal("Content Safety", OpenAiModerationEvaluator.ContentSafetyMetricName);
    }

    /// <summary>
    /// D13 names thirteen category flags, and this counts them.
    /// </summary>
    /// <remarks>
    /// The run-time path never indexes by this list. It enumerates whatever the endpoint returned, so
    /// a fourteenth category reaches the audit chain with no code change. The list is here so the
    /// vocabulary of the vendor is written down once, with its slashes and hyphens intact.
    /// </remarks>
    [Fact]
    public void TheEvaluatorNamesTheThirteenCategoriesD13Counts()
        => Assert.Equal(
            [
                "harassment",
                "harassment/threatening",
                "hate",
                "hate/threatening",
                "illicit",
                "illicit/violent",
                "self-harm",
                "self-harm/instructions",
                "self-harm/intent",
                "sexual",
                "sexual/minors",
                "violence",
                "violence/graphic",
            ],
            OpenAiModerationEvaluator.Categories);

    [Fact]
    public async Task ItPostsTheTextToTheModerationsRouteWithTheOmniModel()
    {
        List<string> bodies = [];
        using var endpoint = Answering(bodies, HttpStatusCode.OK, Clean);
        var evaluator = await BuildAsync(endpoint);

        await evaluator.EvaluateAsync([], Response(Reply), cancellationToken: Token);

        var request = Assert.Single(endpoint.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/v1/moderations", request.RequestUri.AbsolutePath);

        using var body = JsonDocument.Parse(Assert.Single(bodies));
        Assert.Equal("omni-moderation-latest", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(Reply, body.RootElement.GetProperty("input").GetString());

        // One string, and no second field. D13 asks for one POST and nothing beside it.
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task ACleanTextPassesAndCarriesAVerdictWithNoCategory()
    {
        var result = await EvaluateAsync(Clean);
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.True(metric.Value);
        Assert.Equal(EvaluationRating.Exceptional, metric.Interpretation!.Rating);
        Assert.False(metric.Interpretation.Failed);
        Assert.True(ModerationVerdict.TryRead(result, out var verdict));
        Assert.False(verdict!.Flagged);
        Assert.Empty(verdict.Categories);
    }

    [Fact]
    public async Task AFlaggedTextFailsAndNamesEveryFlaggedCategory()
    {
        var result = await EvaluateAsync(Flagged);
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.False(metric.Value);
        Assert.Equal(EvaluationRating.Unacceptable, metric.Interpretation!.Rating);
        Assert.True(metric.Interpretation.Failed);
        Assert.True(ModerationVerdict.TryRead(result, out var verdict));
        Assert.True(verdict!.Flagged);
        Assert.Equal(["harassment", "harassment/threatening", "violence"], verdict.Categories);
    }

    /// <summary>
    /// The categories keep the order the endpoint returned them in.
    /// </summary>
    /// <remarks>
    /// <c>AuditPayloadKeys.ModerationCategories</c> promises that order to a reader of the audit
    /// chain, so the reader enumerates the JSON object and sorts nothing.
    /// </remarks>
    [Fact]
    public async Task TheVerdictKeepsTheOrderTheEndpointReturned()
    {
        var result = await EvaluateAsync(
            """
            { "results": [ { "flagged": true, "categories": { "violence": true, "harassment": true } } ] }
            """);

        Assert.True(ModerationVerdict.TryRead(result, out var verdict));
        Assert.Equal(["violence", "harassment"], verdict!.Categories);
    }

    /// <summary>
    /// The wire types <c>illicit</c> and <c>illicit/violent</c> as nullable, and a null is not a flag.
    /// </summary>
    /// <remarks>
    /// A reader that calls <c>GetBoolean()</c> on those two keys throws on a body OpenAI is allowed to
    /// send. This reader tests for <c>JsonValueKind.True</c> and treats every other kind as not
    /// flagged.
    /// </remarks>
    [Fact]
    public async Task ANullIllicitFlagIsNotAFlag()
    {
        var result = await EvaluateAsync(
            """
            { "results": [ { "flagged": false, "categories": { "illicit": null, "illicit/violent": null } } ] }
            """);

        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.True(metric.Value);
        Assert.Null(metric.Diagnostics);
        Assert.True(ModerationVerdict.TryRead(result, out var verdict));
        Assert.Empty(verdict!.Categories);
    }

    [Fact]
    public async Task AnUnknownCategoryIsRecordedRatherThanDropped()
    {
        var result = await EvaluateAsync(
            """
            { "results": [ { "flagged": true, "categories": { "political/extremism": true } } ] }
            """);

        Assert.True(ModerationVerdict.TryRead(result, out var verdict));
        Assert.Equal(["political/extremism"], verdict!.Categories);
    }

    /// <summary>
    /// The endpoint owns the verdict, and this reader never recomputes it from the flags.
    /// </summary>
    /// <remarks>
    /// Recomputing would drift the day OpenAI moves a threshold, and a model that flags without
    /// naming a category would then read as clean.
    /// </remarks>
    [Fact]
    public async Task ItReadsTheEndpointVerdictAndDoesNotRecomputeIt()
    {
        var result = await EvaluateAsync(
            """
            { "results": [ { "flagged": true, "categories": { "harassment": false, "violence": false } } ] }
            """);

        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.False(metric.Value);
        Assert.True(metric.Interpretation!.Failed);
        Assert.True(ModerationVerdict.TryRead(result, out var verdict));
        Assert.True(verdict!.Flagged);
        Assert.Empty(verdict.Categories);
    }

    [Fact]
    public async Task TheFlaggedCategoryScoresReachTheMetadata()
    {
        var result = await EvaluateAsync(Flagged);
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.NotNull(metric.Metadata);
        Assert.Equal("omni-moderation-latest", metric.Metadata["model"]);
        Assert.Equal("0.9971", metric.Metadata["violence"]);
        Assert.Equal("0.5216", metric.Metadata["harassment"]);

        // A category the endpoint did not flag carries no score, because nothing reads it.
        Assert.False(metric.Metadata.ContainsKey("hate"));
    }

    [Fact]
    public async Task AnEmptyTextCostsNoRequestAndPasses()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
        var evaluator = await BuildAsync(endpoint);

        var result = await evaluator.EvaluateAsync([], Response("   "), cancellationToken: Token);
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.Empty(endpoint.Requests);
        Assert.True(metric.Value);
        Assert.False(metric.Interpretation!.Failed);
        Assert.True(ModerationVerdict.TryRead(result, out var verdict));
        Assert.False(verdict!.Flagged);
        Assert.Empty(verdict.Categories);
    }

    /// <summary>
    /// A vendor outage is inconclusive, and it is never an exception.
    /// </summary>
    /// <remarks>
    /// D9 says a judge must never block a turn, and moderation runs on every turn. A throw would make
    /// an OpenAI outage a caller-facing failure. No verdict is attached, so the caller reads
    /// "unchecked" and not "checked and clean".
    /// </remarks>
    [Fact]
    public async Task AFailedRequestIsInconclusiveAndNeverThrows()
    {
        var result = await EvaluateAsync(HttpStatusCode.InternalServerError, "{}");
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.False(metric.Interpretation.Failed);
        Assert.Equal(EvaluationDiagnosticSeverity.Error, Assert.Single(metric.Diagnostics!).Severity);
        Assert.False(ModerationVerdict.TryRead(result, out var verdict));
        Assert.Null(verdict);
    }

    [Fact]
    public async Task ARateLimitIsInconclusiveAndNamesTheStatus()
    {
        var result = await EvaluateAsync(HttpStatusCode.TooManyRequests, "{}");
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.False(metric.Interpretation.Failed);

        var diagnostic = Assert.Single(metric.Diagnostics!);
        Assert.Equal(EvaluationDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("429", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutIsInconclusiveAndNeverThrows()
    {
        using var endpoint = StubHttpMessageHandler.TimingOut();
        var evaluator = await BuildAsync(endpoint);

        var result = await evaluator.EvaluateAsync([], Response(Reply), cancellationToken: Token);
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.False(metric.Interpretation.Failed);
        Assert.Equal(EvaluationDiagnosticSeverity.Error, Assert.Single(metric.Diagnostics!).Severity);
        Assert.False(ModerationVerdict.TryRead(result, out _));
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIsInconclusive()
    {
        var result = await EvaluateAsync("not json");
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.Equal(EvaluationDiagnosticSeverity.Error, Assert.Single(metric.Diagnostics!).Severity);
        Assert.False(ModerationVerdict.TryRead(result, out _));
    }

    [Fact]
    public async Task AnEmptyResultsArrayIsInconclusive()
    {
        var result = await EvaluateAsync("""{ "results": [] }""");
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.Equal(EvaluationDiagnosticSeverity.Error, Assert.Single(metric.Diagnostics!).Severity);
        Assert.False(ModerationVerdict.TryRead(result, out _));
    }

    [Fact]
    public async Task AResultWithNoFlaggedFieldIsInconclusive()
    {
        var result = await EvaluateAsync("""{ "results": [ { "categories": { "hate": true } } ] }""");
        var metric = result.Get<BooleanMetric>(OpenAiModerationEvaluator.ContentSafetyMetricName);

        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.False(ModerationVerdict.TryRead(result, out _));
    }

    /// <summary>
    /// A cancel is the decision of the caller, and it is not a vendor failure.
    /// </summary>
    /// <remarks>
    /// The evaluator tells a deadline from a cancel by testing the token inside the exception filter,
    /// so a timeout becomes a diagnostic and this stays an exception.
    /// </remarks>
    [Fact]
    public async Task ACallerCancelStillThrows()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
        var evaluator = await BuildAsync(endpoint);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await evaluator.EvaluateAsync(
                [],
                Response(Reply),
                cancellationToken: new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ANullResponseIsRefused()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
        var evaluator = await BuildAsync(endpoint);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await evaluator.EvaluateAsync([], null!, cancellationToken: Token));
    }

    /// <summary>
    /// The build opens the named client on the pipeline of the host, and reads the shared OpenAI key.
    /// </summary>
    /// <remarks>
    /// D13 says one key serves all four OpenAI calls, so this adapter declares no name of its own and
    /// asks for the constant <c>OpenAiChatClientAdapter</c> owns.
    /// </remarks>
    [Fact]
    public async Task CreateAsyncOpensTheNamedClientAndReadsTheSharedOpenAiSecret()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
        StubHandlerFactory pipeline = new(endpoint);
        MapSecretResolver resolver = new();
        resolver.With(OpenAiChatClientAdapter.ApiKeySecretName, ApiKey);

        var evaluator = await OpenAiModerationEvaluator.CreateAsync(pipeline, resolver, Token);
        await evaluator.EvaluateAsync([], Response(Reply), cancellationToken: Token);

        Assert.Equal(["agentcore.openai.moderation"], pipeline.Names);
        Assert.Equal([OpenAiChatClientAdapter.ApiKeySecretName], resolver.Asked);
        Assert.Equal("Bearer " + ApiKey, Assert.Single(endpoint.Requests).Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task CreateAsyncFallsBackToTheEnvironmentVariable()
    {
        var saved = Environment.GetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, ApiKey);

        try
        {
            using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
            StubHandlerFactory pipeline = new(endpoint);
            MapSecretResolver resolver = new();

            var evaluator = await OpenAiModerationEvaluator.CreateAsync(pipeline, resolver, Token);
            await evaluator.EvaluateAsync([], Response(Reply), cancellationToken: Token);

            // The chain is asked first, and the variable answers only when the chain holds nothing.
            Assert.Equal([OpenAiChatClientAdapter.ApiKeySecretName], resolver.Asked);
            Assert.Equal("Bearer " + ApiKey, Assert.Single(endpoint.Requests).Headers.Authorization!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, saved);
        }
    }

    [Fact]
    public async Task CreateAsyncRefusesToBuildWithNoKeyAnywhere()
    {
        var saved = Environment.GetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, null);

        try
        {
            using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
            StubHandlerFactory pipeline = new(endpoint);

            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await OpenAiModerationEvaluator.CreateAsync(pipeline, new MapSecretResolver(), Token));

            Assert.Contains(OpenAiChatClientAdapter.ApiKeySecretName, failure.Message, StringComparison.Ordinal);
            Assert.Contains(OpenAiChatClientAdapter.ApiKeyVariableName, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiKey, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, saved);
        }
    }

    [Fact]
    public async Task CreateAsyncRefusesAPipelineThatIsNothing()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await OpenAiModerationEvaluator.CreateAsync(null!, new MapSecretResolver(), Token));

    [Fact]
    public async Task CreateAsyncOpensNoSocket()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
        StubHandlerFactory pipeline = new(endpoint);
        MapSecretResolver resolver = new();
        resolver.With(OpenAiChatClientAdapter.ApiKeySecretName, ApiKey);

        _ = await OpenAiModerationEvaluator.CreateAsync(pipeline, resolver, Token);

        // A host with no route to api.openai.com still starts. The first turn is the first request.
        Assert.Empty(endpoint.Requests);
    }

    /// <summary>
    /// The moderation call gives the turn back, rather than holding it for the default 100 seconds.
    /// </summary>
    [Fact]
    public async Task TheDeadlineIsShorterThanTheShippedDefault()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Clean);
        MapSecretResolver resolver = new();
        resolver.With(OpenAiChatClientAdapter.ApiKeySecretName, ApiKey);

        var evaluator = await OpenAiModerationEvaluator.CreateAsync(
            new StubHandlerFactory(endpoint),
            resolver,
            Token);

        Assert.Equal(OpenAiModerationEvaluator.ModerationDeadline, evaluator.Deadline);
        Assert.True(OpenAiModerationEvaluator.ModerationDeadline < TimeSpan.FromSeconds(100));
    }

    /// <summary>
    /// Every request lands on the injected <see cref="HttpClient"/> exactly once, and the SDK's own
    /// retry loop never adds a second attempt of its own on top of it.
    /// </summary>
    /// <remarks>
    /// <c>AgentCoreHttpClients</c> is the one place D13 gives retry to. A vendor SDK that retried on
    /// top of that pipeline would multiply the vendor's load and, because each of its own retries gets
    /// a fresh deadline budget, could stretch a single check well past
    /// <see cref="OpenAiModerationEvaluator.ModerationDeadline"/>. This count is the proof that
    /// <c>OpenAIClientOptions.RetryPolicy</c> is turned off, for an error status the endpoint itself
    /// answered with.
    /// </remarks>
    [Fact]
    public async Task ARequestReachesTheInjectedHttpClientExactlyOnceEvenOnAFailure()
    {
        using var endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.TooManyRequests, "{}");
        var evaluator = await BuildAsync(endpoint);

        await evaluator.EvaluateAsync([], Response(Reply), cancellationToken: Token);

        Assert.Single(endpoint.Requests);
    }

    private static ChatResponse Response(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>Builds the evaluator the way a host does, over a stub in place of the network.</summary>
    /// <remarks>
    /// The evaluator takes its <see cref="OpenAI.Moderations.ModerationClient"/> from an internal
    /// constructor for exactly this reason: every test reaches the network zero times, and this is
    /// the one place that trades an <see cref="HttpMessageHandler"/> for one.
    /// </remarks>
    private static async Task<OpenAiModerationEvaluator> BuildAsync(HttpMessageHandler handler)
    {
        StubHandlerFactory pipeline = new(handler);
        MapSecretResolver resolver = new();
        resolver.With(OpenAiChatClientAdapter.ApiKeySecretName, ApiKey);

        return await OpenAiModerationEvaluator.CreateAsync(pipeline, resolver, Token);
    }

    private static ValueTask<EvaluationResult> EvaluateAsync(string body)
        => EvaluateAsync(HttpStatusCode.OK, body);

    private static async ValueTask<EvaluationResult> EvaluateAsync(HttpStatusCode status, string body)
    {
        using var endpoint = StubHttpMessageHandler.Answering(status, body);
        var evaluator = await BuildAsync(endpoint);

        return await evaluator.EvaluateAsync([], Response(Reply), cancellationToken: Token);
    }

    private static StubHttpMessageHandler Answering(List<string> bodies, HttpStatusCode status, string answer)
        => new(request =>
        {
            bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json"),
            };
        });
}

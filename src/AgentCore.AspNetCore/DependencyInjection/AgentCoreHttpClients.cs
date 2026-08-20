using System.Collections.Frozen;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// The one outbound HTTP pipeline this host sends every vendor request on.
/// </summary>
public sealed class AgentCoreHttpClients : IHttpClientFactory, IHttpMessageHandlerFactory, IDisposable
{
    /// <summary>The deadline of one call, over every attempt this pipeline makes.</summary>
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(100);

    /// <summary>How long a pooled connection is kept before it is opened again.</summary>
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>The wait before the first retry. Each later one waits about twice the last.</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>The number of attempts one request gets, including the first.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The name the resilience handler of this pipeline reports as.</summary>
    public const string PipelineName = "agentcore-http";

    private readonly ServiceProvider _provider;

    private readonly IHttpClientFactory _clients;

    /// <summary>Builds the pipeline.</summary>
    /// <param name="primaryHandler">
    /// The handler every client of this pipeline sends on, or <see langword="null"/> for the pooled
    /// default. A caller that passes one keeps it: this pipeline neither rotates nor disposes it.
    /// </param>
    /// <param name="loggers">
    /// The factory the retry writes its lines to, or <see langword="null"/> to write nowhere.
    /// </param>
    public AgentCoreHttpClients(HttpMessageHandler? primaryHandler = null, ILoggerFactory? loggers = null)
    {
        ServiceCollection services = new();

        services.AddLogging();
        if (loggers is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(loggers));
        }

        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigureHttpClient(client => client.Timeout = RequestDeadline);

            if (primaryHandler is null)
            {
                builder.ConfigurePrimaryHttpMessageHandler(
                    () => new SocketsHttpHandler { PooledConnectionLifetime = ConnectionLifetime });
            }
            else
            {
                // The caller owns this handler, so the rotation that would dispose it is turned off.
                builder.ConfigurePrimaryHttpMessageHandler(() => primaryHandler)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            }

            builder.AddResilienceHandler(PipelineName, Retry);
        });

        _provider = services.BuildServiceProvider();
        _clients = _provider.GetRequiredService<IHttpClientFactory>();
    }

    /// <summary>Opens one client of this pipeline.</summary>
    /// <param name="name">The vendor name, such as <c>agentcore.zilliz</c>. Any name is served.</param>
    /// <returns>The client. It carries the deadline and the retry, and no base address.</returns>
    public HttpClient CreateClient(string name) => _clients.CreateClient(name);

    /// <summary>Opens the handler chain of one client, for a caller that adds a handler of its own.</summary>
    /// <param name="name">The vendor name, such as <c>agentcore.zilliz</c>. Any name is served.</param>
    /// <returns>The chain. This pipeline owns it, so a caller never disposes it.</returns>
    public HttpMessageHandler CreateHandler(string name)
        => ((IHttpMessageHandlerFactory)_clients).CreateHandler(name);

    /// <summary>Closes the pipeline. A handler the caller passed in is left open.</summary>
    public void Dispose() => _provider.Dispose();

    /// <summary>Reads the wait one answer asked for, in either form the header takes.</summary>
    /// <param name="response">The answer, or <see langword="null"/> when the attempt threw.</param>
    /// <returns>The wait, or <see langword="null"/> to leave the backoff of this pipeline in place.</returns>
    internal static TimeSpan? RetryAfter(HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is not { } asked)
        {
            return null;
        }

        if (asked.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (asked.Date is { } until)
        {
            var wait = until - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                return wait;
            }
        }

        return null;
    }

    /// <summary>The methods this pipeline retries. Every other method, POST and PATCH included, is sent once.</summary>
    /// <remarks>
    /// GET, HEAD, OPTIONS, and TRACE never write, and PUT and DELETE are defined to be idempotent even
    /// though they do. A <c>kind: http</c> tool that POSTs must not fire its side effect more than
    /// once, so POST and PATCH — and any method this pipeline does not recognize — are never retried,
    /// regardless of how the attempt failed.
    /// </remarks>
    private static readonly FrozenSet<HttpMethod> IdempotentMethods = new[]
    {
        HttpMethod.Get,
        HttpMethod.Head,
        HttpMethod.Options,
        HttpMethod.Trace,
        HttpMethod.Put,
        HttpMethod.Delete,
    }.ToFrozenSet();

    /// <summary>Adds the one retry strategy every client of this pipeline shares.</summary>
    /// <param name="builder">The strategy builder of this client.</param>
    private static void Retry(ResiliencePipelineBuilder<HttpResponseMessage> builder)
        => builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = MaxAttempts - 1,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = FirstRetryDelay,
            ShouldHandle = ShouldRetry,
            DelayGenerator = arguments => ValueTask.FromResult(RetryAfter(arguments.Outcome.Result)),
        });

    /// <summary>Decides whether one failed attempt is worth repeating.</summary>
    /// <param name="arguments">The outcome of the attempt, and the context it ran in.</param>
    /// <returns><see langword="true"/> when the failure is transient and the method is safe to repeat.</returns>
    private static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> arguments)
    {
        var method = arguments.Context.GetRequestMessage()?.Method;
        if (method is null || !IdempotentMethods.Contains(method))
        {
            return ValueTask.FromResult(false);
        }

        var outcome = arguments.Outcome;
        var isTransientFailure = (outcome.Result is { } response && IsTransient(response))
            || outcome.Exception is HttpRequestException
            || outcome.Exception is TaskCanceledException;
        return ValueTask.FromResult(isTransientFailure);
    }

    /// <summary>Reads whether one answer is worth sending again.</summary>
    /// <param name="response">The answer.</param>
    /// <returns><see langword="true"/> when the endpoint failed rather than refused.</returns>
    private static bool IsTransient(HttpResponseMessage response)
        => (int)response.StatusCode >= 500
            || response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.RequestTimeout;
}

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
/// <remarks>
/// <para>
/// One place owns what no vendor should repeat: the connection lifetime, the deadline, and the
/// retry. A vendor adapter owns the rest — its base address, its credential, and its wire format —
/// so a new vendor adds a client name and no policy of its own.
/// </para>
/// <para>
/// <b>This is an object and not an <c>IServiceCollection</c> extension, because of when it runs.</b>
/// <see cref="AgentCoreServiceCollectionExtensions.AddAgentCoreAsync"/> builds every adapter while
/// the host starts, which is before <c>builder.Build()</c> and therefore before any provider exists.
/// An adapter that asked the container for a client at that moment would find none. So the host
/// builds this first, hands it to the adapters that need it, and registers it as a singleton, which
/// is what closes it when the host stops.
/// </para>
/// <para>
/// The pipeline holds a container of its own for exactly one registration: the client factory. Every
/// name it is asked for gets the same defaults, so <c>CreateClient</c> needs no registration in
/// advance and a vendor names its client wherever the vendor is written.
/// </para>
/// <para>
/// A client this returns lives as long as the host does. Set its <c>BaseAddress</c> once and keep
/// it: <see cref="ConnectionLifetime"/> is what closes a pooled connection, so a cluster that moves
/// is reached at its new address without any client being rebuilt.
/// </para>
/// </remarks>
public sealed class AgentCoreHttpClients : IHttpClientFactory, IHttpMessageHandlerFactory, IDisposable
{
    /// <summary>The deadline of one call, over every attempt this pipeline makes.</summary>
    /// <remarks>
    /// The shipped default is 100 seconds. A call this host answers on is a telephone call, and a
    /// tool that has said nothing for half a minute has already lost the caller.
    /// </remarks>
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

    /// <summary>How long a pooled connection is kept before it is opened again.</summary>
    /// <remarks>
    /// This is what makes a long-lived client read DNS again. A vendor that moves its endpoint is
    /// then followed within this window, and no client is rebuilt.
    /// </remarks>
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
    /// <remarks>
    /// An adapter whose credential is read at startup builds its own authorization handler over this
    /// chain. It therefore keeps the pooling and the retry, and its key stays out of every class
    /// that sends a request.
    /// </remarks>
    public HttpMessageHandler CreateHandler(string name)
        => ((IHttpMessageHandlerFactory)_clients).CreateHandler(name);

    /// <summary>Closes the pipeline. A handler the caller passed in is left open.</summary>
    public void Dispose() => _provider.Dispose();

    /// <summary>Reads the wait one answer asked for, in either form the header takes.</summary>
    /// <param name="response">The answer, or <see langword="null"/> when the attempt threw.</param>
    /// <returns>The wait, or <see langword="null"/> to leave the backoff of this pipeline in place.</returns>
    /// <remarks>
    /// Guessing shorter than the endpoint asked for gets the next attempt refused as well, and
    /// guessing longer holds the call for no reason. A date that has already passed asks for nothing.
    /// </remarks>
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

    /// <summary>Adds the one retry strategy every client of this pipeline shares.</summary>
    /// <param name="builder">The strategy builder of this client.</param>
    private static void Retry(ResiliencePipelineBuilder<HttpResponseMessage> builder)
        => builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = MaxAttempts - 1,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = FirstRetryDelay,
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .HandleResult(IsTransient)
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>(),
            DelayGenerator = arguments => ValueTask.FromResult(RetryAfter(arguments.Outcome.Result)),
        });

    /// <summary>Reads whether one answer is worth sending again.</summary>
    /// <param name="response">The answer.</param>
    /// <returns><see langword="true"/> when the endpoint failed rather than refused.</returns>
    /// <remarks>
    /// A refusal is the answer. Repeating it costs a turn and changes nothing, so only a failure of
    /// the endpoint, a rate limit, and a timeout come back here.
    /// </remarks>
    private static bool IsTransient(HttpResponseMessage response)
        => (int)response.StatusCode >= 500
            || response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.RequestTimeout;
}

using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.Sessions;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// The composition root. It turns one document into the services a host resolves.
/// </summary>
public static class AgentCoreServiceCollectionExtensions
{
    /// <summary>Registers everything a call needs, and loads the document when the host starts.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configure">Binds the document and the adapters the document names.</param>
    /// <returns>The same collection, so a host chains its calls.</returns>
    public static IServiceCollection AddAgentCore(
        this IServiceCollection services,
        Action<AgentCoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions();
        services.Configure(configure);
        services.AddLogging();

        services.AddSingleton<AgentCoreBoot>();
        services.AddHostedService<AgentCoreBootService>();

        services.AddSingleton(Boot(boot => boot.Configuration));
        services.AddSingleton(Boot(boot => boot.Secrets));
        services.AddSingleton(Boot(boot => boot.Bindings));
        services.AddSingleton(Boot(boot => boot.CompiledRegistry));
        services.AddSingleton(Boot(boot => boot.Compiled));
        services.AddSingleton(Boot(boot => boot.ChatClients));
        services.AddSingleton(Boot(boot => boot.Guards));
        services.AddSingleton(Boot(boot => boot.Tools));
        services.AddSingleton(Boot(boot => boot.Transcript));
        services.AddSingleton(Boot(boot => boot.Sessions));
        services.AddSingleton(Boot(boot => boot.Agent));
        services.AddSingleton(Boot(boot => boot.AuditQueue));

        // Through the concrete registration, so one factory builds the queue and both service types
        // answer with the same instance.
        services.AddSingleton<IAuditSinkPort>(provider => provider.GetRequiredService<QueuedAuditSink>());

        // Each of these is null when the host registered no vendor for it, and a factory that
        // returns null makes GetService answer null — which is what a caller of an optional seam
        // reads them with.
        services.AddSingleton(Boot(boot => boot.Telemetry!));
        services.AddSingleton(Boot(boot => boot.CallAdapters!));
        services.AddSingleton(Boot(boot => boot.SpeechAdapters!));

        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<AgentCoreOptions>>().Value.TimeProvider
            ?? TimeProvider.System);

        services.TryAddSingleton<ICallSessions>(provider => new InMemoryCallSessions(
            provider.GetRequiredService<ICallSessionFactory>(),
            InMemoryCallSessions.DefaultIdleTimeout,
            provider.GetRequiredService<TimeProvider>()));

        services.AddHostedService(provider => new CallSessionSweeper(
            provider,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<CallSessionSweeper>()
                ?? NullLogger<CallSessionSweeper>.Instance));

        services.TryAddSingleton(Boot(boot => boot.Evaluators));
        services.TryAddSingleton(provider => new EvaluationSampler(
            provider.GetRequiredService<AgentCoreConfiguration>().Evaluation?.SampleRate
            ?? EvaluationConfiguration.DefaultSampleRate));
        services.TryAddSingleton<IEvaluationScorePublisher, InMemoryEvaluationScorePublisher>();

        return services;
    }

    /// <summary>Registers WebSocket options that suit a phone call rather than a browser tab.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <returns>The same collection, so a host chains its calls.</returns>
    public static IServiceCollection AddAgentCoreWebSockets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddWebSockets(options =>
        {
            options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        });
    }

    /// <summary>Reads one thing out of the boot, once the host has started it.</summary>
    /// <typeparam name="T">What the caller is registering.</typeparam>
    /// <param name="read">Picks it off the started boot.</param>
    /// <returns>A factory the container calls on first resolve.</returns>
    private static Func<IServiceProvider, T> Boot<T>(Func<AgentCoreBoot, T> read)
        where T : class
        => provider =>
        {
            var boot = provider.GetRequiredService<AgentCoreBoot>();
            return boot.Release(read(boot));
        };
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Sessions;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// The composition root. It turns one document into the services a host resolves.
/// </summary>
public static class AgentCoreServiceCollectionExtensions
{
    /// <summary>Loads one document and registers everything a call needs to run on it.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configure">Binds the document and the adapters the document names.</param>
    /// <param name="cancellationToken">Cancels the start: the secret reads and the adapter builds.</param>
    /// <returns>The same collection, so a host chains its calls.</returns>
    /// <exception cref="InvalidOperationException">
    /// The options name no document, name two, or bind no chat client adapter.
    /// </exception>
    /// <exception cref="ConfigurationLoadException">
    /// The document fails one of the eight checks, names a knowledge <c>kind</c> no registered
    /// adapter serves, or does not compile.
    /// </exception>
    /// <exception cref="SecretResolutionException">One <c>${secret:name}</c> reference resolves to nothing.</exception>
    public static async ValueTask<IServiceCollection> AddAgentCoreAsync(
        this IServiceCollection services,
        Action<AgentCoreOptions> configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        AgentCoreOptions options = new();
        configure(options);

        ILoggerFactory loggers = options.LoggerFactory ?? NullLoggerFactory.Instance;

        var configuration = ConfigurationStartup.Load(options);

        await TelemetryStartup
            .StartAsync(services, configuration, options, loggers, cancellationToken)
            .ConfigureAwait(false);

        var secrets = await SecretsStartup
            .ResolveAsync(configuration, options, cancellationToken)
            .ConfigureAwait(false);

        AgentCoreStartup startup = new(configuration, secrets);

        var knowledge = await KnowledgeStartup
            .OpenAsync(configuration, options, cancellationToken)
            .ConfigureAwait(false);

        var chatClients = await ChatClientStartup
            .BuildAsync(options, startup, cancellationToken)
            .ConfigureAwait(false);

        var toolsBuilt = await ToolRegistryStartup
            .BuildAsync(options, startup, knowledge, chatClients, configuration, cancellationToken)
            .ConfigureAwait(false);
        var tools = toolsBuilt.Registry;

        var transcript = await TranscriptStartup
            .OpenAsync(configuration, options, loggers, cancellationToken)
            .ConfigureAwait(false);

        var evaluators = await EvaluationStartup
            .CreateRegistryAsync(configuration, options, cancellationToken)
            .ConfigureAwait(false);

        var graph = await CompilationStartup
            .CompileAsync(configuration, chatClients, tools, transcript, evaluators, loggers)
            .ConfigureAwait(false);

        services.AddSingleton(configuration);
        services.AddSingleton(secrets);

        CallSeamStartup.Register(services, configuration, options);

        services.AddSingleton(options.Bindings);
        services.AddSingleton(graph.Registry);
        services.AddSingleton(graph.Compiled);
        services.AddSingleton<IChatClientFactory>(_ => graph.ChatClients);
        services.AddSingleton(tools);
        services.AddSingleton<IGuardEvaluator>(graph.Guards);
        services.AddSingleton(transcript);
        services.AddSingleton(transcript.GetType(), transcript);

        StartupResourceOwner.Own(services, transcript);

        if (toolsBuilt.McpSource is { } mcpSource)
        {
            StartupResourceOwner.Own(services, mcpSource);
        }

        await CallSessionStartup
              .RegisterAsync(services, configuration, options, graph, loggers, cancellationToken)
              .ConfigureAwait(false);

        services.TryAddSingleton(options.TimeProvider ?? TimeProvider.System);

        services.TryAddSingleton<ICallSessions>(provider => new InMemoryCallSessions(
            provider.GetRequiredService<ICallSessionFactory>(),
            InMemoryCallSessions.DefaultIdleTimeout,
            provider.GetRequiredService<TimeProvider>()));

        services.AddHostedService(provider => new CallSessionSweeper(
            provider.GetRequiredService<ICallSessions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<CallSessionSweeper>()
                ?? NullLogger<CallSessionSweeper>.Instance));

        EvaluationStartup.Register(services, configuration, evaluators);

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
}

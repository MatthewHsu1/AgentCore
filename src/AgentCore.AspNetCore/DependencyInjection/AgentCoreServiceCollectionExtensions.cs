using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
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

        // Step 0: take the loggers. Every seam below is bound while the host starts, so nothing can
        // be resolved from a provider yet. A host that bound no factory gets loggers that write
        // nowhere, and the library never throws for want of one.
        ILoggerFactory loggers = options.LoggerFactory ?? NullLoggerFactory.Instance;

        // Steps 1 and 2: load, then validate.
        var configuration = ConfigurationStartup.Load(options);

        // Step 2b: start the telemetry export, before anything below writes a line worth keeping.
        await TelemetryStartup
            .StartAsync(services, configuration, options, loggers, cancellationToken)
            .ConfigureAwait(false);

        // Step 3: resolve every secret once.
        var secrets = await SecretsStartup
            .ResolveAsync(configuration, options, cancellationToken)
            .ConfigureAwait(false);

        AgentCoreStartup startup = new(configuration, secrets);

        // Step 3b: open the knowledge base the document names.
        var knowledge = await KnowledgeStartup
            .OpenAsync(configuration, options, cancellationToken)
            .ConfigureAwait(false);

        // Step 4: build the tool factory chain.
        var tools = ToolFactoryStartup.Build(options, startup, knowledge);

        // Step 4b: open store 1. The compile below builds the history provider around it, and that
        // provider is one instance for the whole process under R7.
        var transcript = await TranscriptStartup
            .OpenAsync(configuration, options, loggers, cancellationToken)
            .ConfigureAwait(false);

        // The evaluator registry is built before the compile, because the moderator that guards each
        // agent's chat pipeline comes out of it and is wired in at compile time.
        var evaluators = await EvaluationStartup
            .CreateRegistryAsync(configuration, options, cancellationToken)
            .ConfigureAwait(false);

        // Step 5: compile. The registry compiles once and every call shares the result.
        var graph = await CompilationStartup
            .CompileAsync(configuration, options, startup, tools, transcript, evaluators, loggers, cancellationToken)
            .ConfigureAwait(false);

        // Step 6: register. Everything above is shared and read-only for the life of the process.
        services.AddSingleton(configuration);
        services.AddSingleton(secrets);

        CallSeamStartup.Register(services, configuration, options);

        services.AddSingleton(options.Bindings);
        services.AddSingleton(graph.Registry);
        services.AddSingleton(graph.Compiled);
        services.AddSingleton<IChatClientFactory>(_ => graph.ChatClients);
        services.AddSingleton<IAgentToolFactory>(tools);
        services.AddSingleton<IGuardEvaluator>(graph.Guards);

        // Under the port, so a host reads the seam it bound, and under its own type, so a test or an
        // operator asks the thing that holds the rows — the in-process store's Read is how the words
        // of one call are read back when no database is named.
        services.AddSingleton(transcript);
        services.AddSingleton(transcript.GetType(), transcript);

        await CallSessionStartup
            .RegisterAsync(services, configuration, options, graph, loggers, cancellationToken)
            .ConfigureAwait(false);

        // The same clock CallSessionFactory already reads off options.TimeProvider, now resolvable
        // from the request's own service provider too. TelnyxRelayConnection reads it here for its
        // idle deadline, so a test that owns options.TimeProvider owns that deadline as well.
        // TryAdd, matching ICallSessionStore below: a host that registered its own TimeProvider
        // before calling AddAgentCore keeps it, rather than this line silently overriding it —
        // AddSingleton would have made the last registration win regardless of which one a
        // GetRequiredService<TimeProvider>() caller actually wanted.
        services.TryAddSingleton(options.TimeProvider ?? TimeProvider.System);

        // The default store holds every call in this process. A host that registered another one
        // before this call keeps it.
        services.TryAddSingleton<ICallSessionStore, InMemoryCallSessionStore>();

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

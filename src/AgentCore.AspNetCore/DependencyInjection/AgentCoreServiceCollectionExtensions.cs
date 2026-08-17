using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
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
/// <remarks>
/// <para>
/// Everything happens while the host starts, in one order, and each step below owns its own class in
/// <c>DependencyInjection/Startup/</c>: load the document and run checks 2 to 8
/// (<see cref="ConfigurationStartup"/>), start the telemetry export
/// (<see cref="TelemetryStartup"/>), resolve every <c>${secret:name}</c> once
/// (<see cref="SecretsStartup"/>), open the knowledge base the document names
/// (<see cref="KnowledgeStartup"/>), build the tool factory chain
/// (<see cref="ToolFactoryStartup"/>), compile through <see cref="CompiledAgentRegistry"/>
/// (<see cref="CompilationStartup"/>), and register the seam a call arrives on
/// (<see cref="CallSeamStartup"/> and <see cref="CallSessionStartup"/>). A configuration defect
/// therefore stops the process with a message that names the fault and points into the document.
/// Nothing is deferred to the first call.
/// </para>
/// <para>
/// The order is the data flow, and it is held by the local variables of
/// <see cref="AddAgentCoreAsync"/> rather than by any list of steps: a step that needs the resolved
/// secrets takes them as a parameter, so a step run out of order does not compile.
/// </para>
/// <para>
/// <see cref="CompiledAgent"/> is a process singleton by design, and it is registered as one.
/// <see cref="CallSession"/> is not, and it is registered nowhere: one call gets one session from
/// <see cref="ICallSessionFactory"/>, and <see cref="ICallSessionStore"/> holds it between requests.
/// </para>
/// <para>
/// This also registers a <see cref="TimeProvider"/> for the whole container —
/// <see cref="AgentCoreOptions.TimeProvider"/> when the host bound one, otherwise
/// <see cref="TimeProvider.System"/> — unless a host already registered its own before calling
/// this method, which it keeps. <see cref="CallSessionFactory"/> reads the same clock
/// directly off <see cref="AgentCoreOptions.TimeProvider"/>, and the relay's idle deadline in
/// <c>AgentCore.AspNetCore/Vendors/TelnyxRelay/</c> resolves this registration, so the two always
/// agree on what time it is.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// This method is async because three of its steps wait: the secret resolution of step 3, the
    /// knowledge seam of step 3b, and the chat client seam of step 5. A top-level <c>Program.cs</c>
    /// awaits it before <c>builder.Build()</c>, and no thread blocks while the host starts.
    /// </remarks>
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

        // Step 5: compile. The registry compiles once and every call shares the result.
        var graph = await CompilationStartup
            .CompileAsync(configuration, options, startup, tools, loggers, cancellationToken)
            .ConfigureAwait(false);

        // The evaluator registry is built before the session factory, because the moderator the turn
        // loop reads comes out of it.
        var evaluators = await EvaluationStartup
            .CreateRegistryAsync(configuration, options, cancellationToken)
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

        await CallSessionStartup
            .RegisterAsync(services, configuration, options, graph, evaluators, loggers, cancellationToken)
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
    /// <remarks>
    /// <para>
    /// The shipped default is a two-minute <c>KeepAliveInterval</c> and no <c>KeepAliveTimeout</c>
    /// at all, so a peer that stopped answering can hold a Kestrel connection, and the call session
    /// behind it, for two minutes before anything notices. A phone call needs a dead peer caught in
    /// seconds, not minutes, so this sets both to about twenty seconds. This is a convenience for
    /// <c>UseWebSockets</c>, not <c>providers.call.idleTimeoutSeconds</c>: the keep-alive ping
    /// only catches a peer the network itself stopped answering, and only that idle timeout catches
    /// a peer that still answers pings but sends no relay frame. A host that wants different numbers
    /// calls <c>services.AddWebSockets(...)</c> itself and skips this method.
    /// </para>
    /// <para>
    /// The host must still call the no-argument <c>app.UseWebSockets()</c>. The overload that takes
    /// a <c>WebSocketOptions</c> instance directly — <c>app.UseWebSockets(new WebSocketOptions())</c>
    /// — never reads this registration at all, so a host that calls this method and then that
    /// overload gets the shipped two-minute defaults back, with no error or warning either way.
    /// </para>
    /// </remarks>
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

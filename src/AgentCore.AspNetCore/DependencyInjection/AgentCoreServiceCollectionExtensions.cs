using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.Sessions;
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
/// Everything happens while the host starts, in one order: load the document, run checks 2 to 8,
/// resolve every <c>${secret:name}</c> once, build the tool factory chain, compile through
/// <see cref="CompiledAgentRegistry"/>, and register the seam a call arrives on. A configuration
/// defect therefore stops the process with a message that names the fault and points into the
/// document. Nothing is deferred to the first call.
/// </para>
/// <para>
/// <see cref="CompiledAgent"/> is a process singleton by design, and it is registered as one.
/// <see cref="CallSession"/> is not, and it is registered nowhere: one call gets one session from
/// <see cref="ICallSessionFactory"/>, and <see cref="ICallSessionStore"/> holds it between requests.
/// </para>
/// <para>
/// <b><c>providers.speech</c> and <c>providers.telephony</c> bind and this method reads neither, on
/// purpose.</b> D28 buys the whole speech layer inside Telnyx Conversation Relay, so
/// <c>providers.speech</c> names the relay and no speech adapter exists to bind: the code that reads
/// it is <c>AgentCore.AspNetCore/Vendors/TelnyxRelay/</c>, an inbound <c>Map*</c> extension that
/// turns relay frames into <see cref="IConversationPort"/> calls, and this scaffold does not ship it.
/// <c>providers.telephony</c> names the vendor behind <c>ITelephonyControlPort</c> —
/// answer, start, conference transfer, hang up — whose adapter is
/// <c>AgentCore.Infrastructure/Telephony/Telnyx/</c>, and that port is not declared yet either.
/// <b>Both are inbound or outbound transports and neither changes agent shape</b>, so binding them
/// here before their adapters exist would register a name that resolves to nothing. Read them in the
/// <c>Map*</c> extension that owns each transport, not in this method. Item 6c also forbids any
/// audio in this solution, and neither adapter will hold one.
/// </para>
/// </remarks>
public static class AgentCoreServiceCollectionExtensions
{
    /// <summary>Loads one document and registers everything a call needs to run on it.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configure">Binds the document and the adapters the document names.</param>
    /// <returns>The same collection, so a host chains its calls.</returns>
    /// <exception cref="InvalidOperationException">
    /// The options name no document, name two, or bind no chat client adapter.
    /// </exception>
    /// <exception cref="ConfigurationLoadException">The document fails one of the eight checks, or does not compile.</exception>
    /// <exception cref="SecretResolutionException">One <c>${secret:name}</c> reference resolves to nothing.</exception>
    public static IServiceCollection AddAgentCore(this IServiceCollection services, Action<AgentCoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        AgentCoreOptions options = new();
        configure(options);

        // Step 0: take the loggers. Every seam below is bound while the host starts, so nothing can
        // be resolved from a provider yet. A host that bound no factory gets loggers that write
        // nowhere, and the library never throws for want of one.
        ILoggerFactory loggers = options.LoggerFactory ?? NullLoggerFactory.Instance;

        // Step 1: load.
        var configuration = LoadDocument(options);

        // Step 2: validate. Checks 2 to 8 report every defect at once, so one start names them all.
        ConfigurationValidator.Validate(configuration);

        // Step 3: resolve every secret once. A tool call then costs no lookup, and a missing
        // credential stops the host rather than one turn.
        var secrets = ResolvedSecrets
            .ResolveAsync(configuration, options.SecretResolver ?? NoSecretResolver.Instance)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        AgentCoreStartup startup = new(configuration, secrets);

        // Step 4: build the tool factory chain. A link answers null for a kind it does not serve, and
        // the composite fails the start when no link serves a kind the document declares.
        var tools = BuildToolFactory(options, startup);

        // Step 5: compile. The registry compiles once and every call shares the result.
        var chatClients = (options.ChatClients
            ?? throw new InvalidOperationException(
                "AddAgentCore binds no chat client adapter. Call options.UseChatClients(...), because the "
                + "compile table asks it for every agent and for the extractor."))
            .Invoke(startup);

        // Section 8.7, row five: a guard that throws at run time is not a defect. The evaluator
        // already reports each distinct guard exactly once, and this is where that report finds a
        // logger. Nothing else binds it, so an unbound evaluator would report into nothing.
        GuardEvaluator guards = new(configuration.Guards, loggers.CreateLogger<GuardEvaluator>());
        CompiledAgentRegistry registry = new();

        // Row 4 of the compile table needs both seams, so both are bound here. The evaluator is
        // shared and holds no state of its own. The state source is CallStateScope, which finds the
        // state of the call running on the current flow of execution, and CallSession opens that
        // scope for the turn. One compiled graph therefore serves every call, exactly as T44 asks,
        // and two calls that run at the same time take different edges.
        var compiled = registry.GetOrCompile(
            configuration,
            new AgentCompilationContext(chatClients)
            {
                Tools = tools,
                Guards = guards,
                StateSnapshot = CallStateScope.Snapshot,
            });

        // Step 6: register. Everything above is shared and read-only for the life of the process.
        // The audit sink and the logger are both optional and both have a working default, so a host
        // that binds neither still answers a call and still produces the events of D23.
        CallSessionFactory sessions = new(
            compiled,
            guards,
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            options.TimeProvider,
            options.AuditSink,
            loggers.CreateLogger<CallSession>());

        services.AddSingleton(configuration);
        services.AddSingleton(secrets);
        services.AddSingleton(options.Bindings);
        services.AddSingleton(registry);
        services.AddSingleton(compiled);
        services.AddSingleton<IChatClientFactory>(_ => chatClients);
        services.AddSingleton<IAgentToolFactory>(tools);
        services.AddSingleton<IGuardEvaluator>(guards);
        services.AddSingleton<ICallSessionFactory>(sessions);

        if (options.AuditSink is { } audit)
        {
            // Only what the host bound is registered. Nothing stands in for the PostgreSQL sink of
            // section 7, because a list in this process holds none of the three defences of D23.
            services.AddSingleton(audit);
        }

        // The default store holds every call in this process. A host that registered another one
        // before this call keeps it.
        services.TryAddSingleton<ICallSessionStore, InMemoryCallSessionStore>();

        AddEvaluation(services, configuration);

        return services;
    }

    /// <summary>Registers the evaluation seam of D13, at the rate the document sets.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The loaded document. It carries <c>evaluation.sampleRate</c>.</param>
    /// <remarks>
    /// <para>
    /// Each registration is a <c>TryAdd</c>, so a host that registered its own registry, its own
    /// sampler, or its own publisher keeps it. That matches how <see cref="ICallSessionStore"/>
    /// is registered above, and it matters most for the publisher: the in-memory one keeps every
    /// score in a list that grows without a bound, so a long-running host replaces it.
    /// </para>
    /// <para>
    /// <b>The sample rate comes from the document, and it defaults to 0.</b> Triage row T18 says the
    /// rate comes from configuration and defers the online path until the offline gate proves the
    /// evaluators, and D9 says a judge must never block a turn. A document that sets no rate
    /// therefore draws no number and calls no evaluator, so the seam is reachable and costs nothing.
    /// The range is checked at load, so the value read here is already good.
    /// </para>
    /// <para>
    /// <c>fault_code</c> is registered because D13 names it and because it calls no model: the
    /// measurement is a set comparison over the reply text. It is the one evaluator that is safe to
    /// register by default.
    /// </para>
    /// </remarks>
    private static void AddEvaluation(IServiceCollection services, AgentCoreConfiguration configuration)
    {
        services.TryAddSingleton(new EvaluatorRegistry().Register("fault_code", new FaultCodeEvaluator()));
        services.TryAddSingleton(new EvaluationSampler(
            configuration.Evaluation?.SampleRate ?? EvaluationConfiguration.DefaultSampleRate));
        services.TryAddSingleton<IEvaluationScorePublisher, InMemoryEvaluationScorePublisher>();
    }

    /// <summary>Reads the one document the options name.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <returns>The loaded document.</returns>
    /// <exception cref="InvalidOperationException">The options name no document, or name two.</exception>
    private static AgentCoreConfiguration LoadDocument(AgentCoreOptions options)
    {
        var hasPath = options.ConfigurationPath is { Length: > 0 };
        if (options.Configuration is { } loaded)
        {
            if (hasPath)
            {
                throw new InvalidOperationException(
                    "AddAgentCore names two documents: options.Configuration holds one and "
                    + "options.ConfigurationPath names another. Set one of the two.");
            }

            return loaded;
        }

        if (!hasPath)
        {
            throw new InvalidOperationException(
                "AddAgentCore names no document. Set options.ConfigurationPath to a .yaml, .yml, or .json "
                + "file, or set options.Configuration to a document the host already loaded.");
        }

        return ConfigurationLoader.LoadFile(options.ConfigurationPath!);
    }

    /// <summary>Builds the one tool factory the compile table asks.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <param name="startup">The loaded document and the resolved secrets.</param>
    /// <returns>The composite, over every link the host bound.</returns>
    private static CompositeAgentToolFactory BuildToolFactory(AgentCoreOptions options, AgentCoreStartup startup)
    {
        List<IAgentToolFactory> links = [];

        // The two knowledge ports bind apart, so one of the two is enough for the link to be worth
        // adding. The link then serves the built-in whose port is bound and fails the load on the
        // built-in whose port is not.
        if (options.KnowledgeRetrieval is not null || options.DocumentStore is not null)
        {
            links.Add(new BuiltinToolFactory(
                options.KnowledgeRetrieval?.Invoke(startup),
                options.DocumentStore?.Invoke(startup)));
        }

        // The binding link needs no adapter. The registry is the seam the host already filled.
        links.Add(new BindingToolFactory(options.Bindings));

        foreach (var extra in options.ToolFactories)
        {
            links.Add(extra(startup));
        }

        return new CompositeAgentToolFactory(links);
    }

    /// <summary>The chain a host that bound none gets: it holds no name at all.</summary>
    /// <remarks>
    /// A document that references no secret resolves cleanly against this. A document that references
    /// one fails, and the failure names the reference and its JSON Pointer.
    /// </remarks>
    private sealed class NoSecretResolver : ISecretResolverPort
    {
        public static NoSecretResolver Instance { get; } = new();

        public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }
}

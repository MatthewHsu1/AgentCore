using AgentCore.Application.Audit;
using AgentCore.Application.Call;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.Call;
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
/// Everything happens while the host starts, in one order: load the document, run checks 2 to 8,
/// resolve every <c>${secret:name}</c> once, open the knowledge base the document names, build the
/// tool factory chain, compile through
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
/// This also registers a <see cref="TimeProvider"/> for the whole container —
/// <see cref="AgentCoreOptions.TimeProvider"/> when the host bound one, otherwise
/// <see cref="TimeProvider.System"/> — unless a host already registered its own before calling
/// this method, which it keeps. <see cref="CallSessionFactory"/> reads the same clock
/// directly off <see cref="AgentCoreOptions.TimeProvider"/>, and the relay's idle deadline in
/// <c>AgentCore.AspNetCore/Vendors/TelnyxRelay/</c> resolves this registration, so the two always
/// agree on what time it is.
/// </para>
/// <para>
/// <b>A transport is owned by the <c>Map*</c> extension that maps it, and that is still true.</b>
/// D28 buys the whole speech layer inside Telnyx Conversation Relay, so <c>providers.speech</c>
/// names the relay: the code that <i>selects</i> it is
/// <c>AgentCore.AspNetCore/Vendors/TelnyxRelay/</c>, an inbound <c>Map*</c> extension that turns relay
/// frames into <see cref="IConversationPort"/> calls, and that extension is what asks
/// <see cref="Application.Providers.VendorAdapterSelector"/> — the one selector every vendor seam
/// shares — whether this document named its vendor.
/// <see cref="AgentCoreOptions.UseSpeech"/> hands the registered vendors over, and this method only
/// puts that list in the container for the <c>Map*</c> extension to resolve.
/// <c>providers.telephony</c> names the vendor behind <c>ITelephonyControlPort</c> —
/// answer, start, conference transfer, hang up — whose adapter is
/// <c>AgentCore.Infrastructure/Telephony/Telnyx/</c>, and that port is not declared yet.
/// Item 6c also forbids any audio in this solution, and neither adapter will hold one.
/// </para>
/// <para>
/// <b>That reasoning held while speech was one optional block, and it does not hold for a
/// consistency rule between two required blocks.</b> <c>providers.call</c> and
/// <c>providers.speech</c> are now required of one another, and whether they agree is a fact about
/// the document rather than about any route: a transport whose frames already carry text performs
/// recognition and synthesis itself, so a document that names it for the pipe and something else
/// for the ears or the mouth describes a deployment that cannot exist. Each role is checked on its
/// own, so a document that gets both wrong hears about both. That is true or false before anything is
/// mapped. So when <see cref="AgentCoreOptions.UseCall"/> registered a transport, this method
/// selects the one <c>providers.call.kind</c> names and runs
/// <see cref="Application.Call.CallSpeechPairing"/> over the pair — precisely so a host that forgets
/// <c>app.MapCall()</c> still learns its document contradicts itself, rather than answering a call
/// that connects and never transcribes. It selects no speech vendor while doing it; the
/// <c>Map*</c> extension still owns that.
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

        // Step 1: load.
        var configuration = LoadDocument(options);

        // Step 2: validate. Checks 2 to 8 report every defect at once, so one start names them all.
        ConfigurationValidator.Validate(configuration);

        // Step 2b: start the telemetry export, before anything below writes a line worth keeping.
        // providers.telemetry.kind picks one of the vendors the host registered, and a document that
        // names none exports nothing.
        //
        // The session is ADDED to the host's factory rather than replacing anything: a deployment
        // reading its console keeps reading its console, and the same line also reaches the
        // collector. AddProvider refreshes the loggers the factory already handed out, so a category
        // created before this line is exported too.
        ITelemetrySession? telemetry = options.Telemetry is { } telemetryAdapters
            ? await TelemetrySessionFactory
                .StartAsync(configuration, options.SecretResolver, telemetryAdapters, cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (telemetry?.Logs is { } telemetryLogs)
        {
            loggers.AddProvider(telemetryLogs);
        }

        if (telemetry is not null)
        {
            // The container owns the shutdown. Disposal is what flushes the batch a stopping process
            // is still holding, so a session nobody disposed would drop it.
            services.AddSingleton(telemetry);
        }

        // Step 3: resolve every secret once. A tool call then costs no lookup, and a missing
        // credential stops the host rather than one turn.
        var secrets = await ResolvedSecrets
            .ResolveAsync(configuration, options.SecretResolver ?? NoSecretResolver.Instance, cancellationToken)
            .ConfigureAwait(false);

        AgentCoreStartup startup = new(configuration, secrets);

        // Step 3b: open the knowledge base the document names. The host listed its knowledge vendors
        // once and providers.knowledge picks one for each port, so both ports are open before any
        // tool is built. A host that registered no vendor keeps two null ports and binds whatever
        // its explicit seams hold.
        //
        // A port an explicit seam already sets is not asked for here at all. The registry then reads
        // neither that field's kind nor its vendor, so an override costs no vendor build and a kind
        // this host does not register cannot fail a start that was never going to use it.
        var knowledge = options.KnowledgeStores is { } stores
            ? await CompositeKnowledgeStoreFactory
                .CreateAsync(
                    configuration,
                    options.SecretResolver,
                    stores,
                    includeSearch: options.KnowledgeRetrieval is null,
                    includeDocuments: options.DocumentStore is null,
                    cancellationToken)
                .ConfigureAwait(false)
            : default;

        // Step 4: build the tool factory chain. A link answers null for a kind it does not serve, and
        // the composite fails the start when no link serves a kind the document declares.
        var tools = BuildToolFactory(options, startup, knowledge);

        // Step 5: compile. The registry compiles once and every call shares the result.
        var chatClients = await (options.ChatClients
            ?? throw new InvalidOperationException(
                "AddAgentCoreAsync binds no chat client adapter. Call options.UseChatClients(...), because the "
                + "compile table asks it for every agent and for the extractor."))
            .Invoke(startup, cancellationToken)
            .ConfigureAwait(false);

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
        // The evaluator registry is built before the session factory, because the moderator the turn
        // loop reads comes out of it. D13 asks for one evaluator used twice: the same object serves
        // the turn loop here and the offline golden set through the registry below.
        EvaluatorRegistry evaluators = new();
        evaluators.Register("fault_code", new FaultCodeEvaluator());

        // providers.moderation.kind picks one of the vendors the host registered. A document that
        // names none moderates nothing, and no adapter is asked to build anything.
        if (options.Moderation is { } moderationAdapters
            && await ModerationEvaluatorFactory
                .CreateAsync(configuration, options.SecretResolver, moderationAdapters, cancellationToken)
                .ConfigureAwait(false) is { } moderation)
        {
            evaluators.Register(PromptModerator.ModerationEvaluatorName, moderation);
        }

        // The one place the "never sit on the turn" rule of IAuditSinkPort is applied. The host binds
        // the store, and this wraps it in the bounded channel and batching background writer of
        // section 7, so a store that blocks on its database is a correct store and no adapter carries
        // a queue of its own. A host that bound nothing gets nothing: there is no store to drain to.
        QueuedAuditSink? auditSink = options.AuditSink is { } store
            ? new QueuedAuditSink(store, loggers.CreateLogger<QueuedAuditSink>())
            : null;

        // Composition, and so it happens here rather than inside the factory. The host bound a sink
        // and a logger and never named an ICallObserver; this is the line that turns those bindings
        // into the readings of a call, in the order CallObservers.Standard argues for.
        var sessionLogger = loggers.CreateLogger<CallSession>();

        CallSessionFactory sessions = new(
            compiled,
            guards,
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            options.TimeProvider,
            sessionLogger,
            PromptModerator.FromRegistry(evaluators),
            CallObservers.Standard(auditSink, sessionLogger, options.Observers));

        services.AddSingleton(configuration);
        services.AddSingleton(secrets);

        if (options.Speech is { } speechAdapters)
        {
            // Registered here, and selected by nothing. providers.speech names the recognition
            // vendor and the synthesis vendor, but no code in this solution turns either name into a
            // constructed adapter: the one transport shipped today carries text, so it is itself both
            // and there is nothing to build. The list goes in the container beside the document
            // above so that a vendor which does need constructing has somewhere to be found.
            //
            // The call seam below reads both providers.speech roles all the same, and that is not a
            // contradiction: it never selects a speech vendor, it only checks that the blocks
            // agree — see this type's own remarks.
            services.AddSingleton<IReadOnlyList<ISpeechAdapter>>(speechAdapters);
        }

        if (options.Call is { } callAdapters)
        {
            // Registering a vendor is what turns this seam on. A host that registered none is not
            // asked what its document says, exactly as telemetry, knowledge, and moderation are not
            // read when a host registered no adapter for them.
            var callEntry = configuration.Providers?.Call
                ?? throw MissingCallBlock();

            var selectedCall = VendorAdapterSelector.Select(
                callEntry.Kind, callAdapters, CallSeams.Call);

            var speechEntry = configuration.Providers?.Speech
                ?? throw MissingSpeechBlock();

            // Read here, and not only where the route is mapped. Agreement between two document
            // entries is a document fact: it is true or false whether or not anything is routed, so
            // a host that forgets app.MapCall() still learns its document contradicts itself.
            CallSpeechPairing.Validate(callEntry, speechEntry, selectedCall);

            services.AddSingleton<IReadOnlyList<ICallAdapter>>(callAdapters);
        }

        services.AddSingleton(options.Bindings);
        services.AddSingleton(registry);
        services.AddSingleton(compiled);
        services.AddSingleton<IChatClientFactory>(_ => chatClients);
        services.AddSingleton<IAgentToolFactory>(tools);
        services.AddSingleton<IGuardEvaluator>(guards);
        services.AddSingleton<ICallSessionFactory>(sessions);

        // The same clock CallSessionFactory already reads off options.TimeProvider, now resolvable
        // from the request's own service provider too. TelnyxRelayConnection reads it here for its
        // idle deadline, so a test that owns options.TimeProvider owns that deadline as well.
        // TryAdd, matching ICallSessionStore below: a host that registered its own TimeProvider
        // before calling AddAgentCore keeps it, rather than this line silently overriding it —
        // AddSingleton would have made the last registration win regardless of which one a
        // GetRequiredService<TimeProvider>() caller actually wanted.
        services.TryAddSingleton(options.TimeProvider ?? TimeProvider.System);

        if (auditSink is { } queue)
        {
            // The QUEUE is what is registered, not the store the host bound. Resolving
            // IAuditSinkPort must give the thing that honours the port's contract, and appending
            // straight to the store would be the one path that sits on the caller. It is registered
            // as the concrete type as well as the port, so a host that wants FlushAsync before it
            // reports success can ask for it, and so the container disposes it on the way out — that
            // disposal is what drains the queue and keeps the accepted rows.
            services.AddSingleton(queue);
            services.AddSingleton<IAuditSinkPort>(queue);
        }

        // The default store holds every call in this process. A host that registered another one
        // before this call keeps it.
        services.TryAddSingleton<ICallSessionStore, InMemoryCallSessionStore>();

        AddEvaluation(services, configuration, evaluators);

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

    /// <summary>Registers the evaluation seam of D13, at the rate the document sets.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The loaded document. It carries <c>evaluation.sampleRate</c>.</param>
    /// <param name="evaluators">
    /// The registry built above, which already holds <c>fault_code</c> and, when the host bound one,
    /// the moderation evaluator. It is built there and not here because the turn loop reads the
    /// moderator out of it, and the session factory is constructed before this method runs.
    /// </param>
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
    /// <para>
    /// <b>Moderation does not pass through <see cref="EvaluationSampler"/>.</b> D13 says the endpoint
    /// is free at any volume and counts against no usage limit, so every turn is checked and
    /// "sampling buys nothing when the call is free". The sampler here governs the judge of T18, and
    /// nothing else.
    /// </para>
    /// </remarks>
    private static void AddEvaluation(
        IServiceCollection services,
        AgentCoreConfiguration configuration,
        EvaluatorRegistry evaluators)
    {
        services.TryAddSingleton(evaluators);
        services.TryAddSingleton(new EvaluationSampler(configuration.Evaluation?.SampleRate ?? EvaluationConfiguration.DefaultSampleRate));
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
                    "AddAgentCoreAsync names two documents: options.Configuration holds one and "
                    + "options.ConfigurationPath names another. Set one of the two.");
            }

            return loaded;
        }

        if (!hasPath)
        {
            throw new InvalidOperationException(
                "AddAgentCoreAsync names no document. Set options.ConfigurationPath to a .yaml, .yml, or .json "
                + "file, or set options.Configuration to a document the host already loaded.");
        }

        return ConfigurationLoader.LoadFile(options.ConfigurationPath!);
    }

    /// <summary>Refuses a configuration that turned the call seam on and named no transport.</summary>
    /// <returns>The failure to throw, pointed at the block that is missing.</returns>
    /// <remarks>
    /// <para>
    /// <c>required: ["call", "speech"]</c> sits on the <c>providers</c> <b>object</b>, and the root
    /// of the schema requires only <c>apiVersion</c> and <c>name</c> — <c>providers</c> itself is
    /// optional. So two routes reach here legitimately: a host that built an
    /// <see cref="AgentCoreConfiguration"/> in code, which passes through no schema at all, and a
    /// valid loaded document that writes no <c>providers</c> section whatsoever.
    /// </para>
    /// <para>
    /// Neither is a schema defect the loader could have caught, so neither may arrive as a
    /// <see cref="NullReferenceException"/> out of the guard below it.
    /// </para>
    /// </remarks>
    private static ConfigurationLoadException MissingCallBlock()
        => new(new ConfigurationError
        {
            Pointer = "/providers/call",
            Message =
                "This host registered a call transport with options.UseCall(...), and this "
                + "configuration writes no providers.call block for a kind to be picked from. A "
                + "document that writes a providers section names both call and speech, because the "
                + "schema requires them there; a document that writes no providers section at all is "
                + "valid, and so is a configuration a host built in code, which passes through no "
                + "schema. Write providers.call: { kind: ... }, or drop the options.UseCall(...) call.",
            Check = ConfigurationCheck.ReferenceResolution,
        });

    /// <summary>Refuses a configuration whose call transport has no speech block to be paired with.</summary>
    /// <returns>The failure to throw, pointed at the block that is missing.</returns>
    /// <remarks>
    /// Reachable by the same two routes as <see cref="MissingCallBlock"/>, and for the same reason:
    /// the schema requires the two blocks of one another and requires the <c>providers</c> object of
    /// nobody. A configuration that named a call transport and no speech vendor cannot be paired, so
    /// it is refused here rather than passed to the guard as a <see langword="null"/>.
    /// </remarks>
    private static ConfigurationLoadException MissingSpeechBlock()
        => new(new ConfigurationError
        {
            Pointer = "/providers/speech",
            Message =
                "This configuration names providers.call and no providers.speech, so there is "
                + "nothing to check the transport against: a transport that carries text is itself "
                + "the recognizer, and one that carries audio needs a speech vendor named. A "
                + "document that writes a providers section names both, because the schema requires "
                + "them there; a document that writes no providers section at all is valid, and so "
                + "is a configuration a host built in code, which passes through no schema. Write "
                + "providers.speech with both of its roles: stt: { kind: ... } and tts: { kind: ... }.",
            Check = ConfigurationCheck.ReferenceResolution,
        });

    /// <summary>Builds the one tool factory the compile table asks.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <param name="startup">The loaded document and the resolved secrets.</param>
    /// <param name="knowledge">The two ports the knowledge registry opened, each one or <see langword="null"/>.</param>
    /// <returns>The composite, over every link the host bound.</returns>
    private static CompositeAgentToolFactory BuildToolFactory(
        AgentCoreOptions options,
        AgentCoreStartup startup,
        (IKnowledgeRetrievalPort? Search, IDocumentStorePort? Documents) knowledge)
    {
        List<IAgentToolFactory> links = [];

        // An explicit UseKnowledgeRetrieval, UseDocumentStore, or UseKnowledge call wins over the
        // UseKnowledgeStores registry, for the port it sets. A host that wants one half of its own
        // and one half from the document writes both calls, and neither one hides the other. Step 3b
        // already left the shadowed half unresolved and unbuilt, so the half it did open is the only
        // one this reads.
        var retrieval = options.KnowledgeRetrieval is { } bound ? bound(startup) : knowledge.Search;
        var documents = options.DocumentStore is { } boundDocuments ? boundDocuments(startup) : knowledge.Documents;

        // The two knowledge ports bind apart, so one of the two is enough for the link to be worth
        // adding. The link then serves the built-in whose port is bound and fails the load on the
        // built-in whose port is not.
        if (retrieval is not null || documents is not null)
        {
            links.Add(new BuiltinToolFactory(retrieval, documents));
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

using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// Everything <c>AddAgentCore</c> needs that the document does not hold.
/// </summary>
/// <remarks>
/// <para>
/// The document names a model, a tool, and a secret. This object binds each of those names to the
/// adapter that runs it, so <c>AgentCore.AspNetCore</c> composes the layers and references no vendor
/// package of its own. The host owns the adapters and hands them over here.
/// </para>
/// <para>
/// Each seam takes a factory over <see cref="AgentCoreStartup"/> rather than a built object, because
/// the document and the resolved secrets are what an adapter needs and neither exists before
/// <c>AddAgentCore</c> runs.
/// </para>
/// </remarks>
public sealed class AgentCoreOptions
{
    private readonly List<Func<AgentCoreStartup, IAgentToolFactory>> _toolFactories = [];
    private readonly List<ICallObserver> _observers = [];

    /// <summary>Gets the path of the configuration document, or <see langword="null"/>.</summary>
    /// <remarks>
    /// The extension picks the format: <c>.yaml</c>, <c>.yml</c>, or <c>.json</c>. Set this or
    /// <see cref="Configuration"/>, and never both.
    /// </remarks>
    public string? ConfigurationPath { get; set; }

    /// <summary>Gets or sets a document the host already loaded, or <see langword="null"/>.</summary>
    /// <remarks>
    /// A host that reads its document from somewhere this library does not know about binds it here.
    /// The document still passes checks 2 to 8 before anything compiles.
    /// </remarks>
    public AgentCoreConfiguration? Configuration { get; set; }

    /// <summary>Gets or sets the chain that reads a <c>${secret:name}</c> reference.</summary>
    /// <remarks>
    /// A document that references no secret needs none. A document that references one and finds no
    /// chain fails at startup, and the message names the reference.
    /// </remarks>
    public ISecretResolverPort? SecretResolver { get; set; }

    /// <summary>Gets or sets the clock this container runs on, or <see langword="null"/> for <see cref="System.TimeProvider.System"/>.</summary>
    /// <remarks>
    /// Three things read this one clock: the reserved <c>callDurationSeconds</c> slot, the relay's
    /// idle deadline in <c>AgentCore.AspNetCore/Vendors/TelnyxRelay/</c>, and — once
    /// <c>AddAgentCore</c> registers it as a container-wide <see cref="System.TimeProvider"/> — any
    /// host code that resolves one. A test that wants a deterministic idle deadline binds a fake
    /// clock here, once, rather than to each reader separately.
    /// </remarks>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>Gets or sets the factory the library takes its loggers from.</summary>
    /// <remarks>
    /// <para>
    /// A host that binds none still runs, and every line the library writes goes nowhere. Section 8.7
    /// says "log once" for three rows, and each of them writes here.
    /// </para>
    /// <para>
    /// This is a factory and not a resolved service, because <c>AddAgentCore</c> loads, validates,
    /// compiles, and binds the guard evaluator while the host starts. There is no service provider to
    /// read at that moment.
    /// </para>
    /// </remarks>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Gets the map from a <c>binds:</c> name to the host delegate behind it.</summary>
    /// <remarks>
    /// The document writes <c>kind: binding</c> with <c>binds: CreateCase</c> and knows nothing else.
    /// <see cref="Bind(string, ToolBinding)"/> is how the host completes that seam.
    /// </remarks>
    public ToolBindingRegistry Bindings { get; } = new();

    /// <summary>Gets the seam that resolves a model reference, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, CancellationToken, ValueTask<IChatClientFactory>>? ChatClients { get; private set; }

    /// <summary>Gets the knowledge vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<IKnowledgeStoreAdapter>? KnowledgeStores { get; private set; }

    /// <summary>Gets the seam <c>knowledge.search</c> ranks with, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, IKnowledgeRetrievalPort>? KnowledgeRetrieval { get; private set; }

    /// <summary>Gets the seam <c>knowledge.read</c> opens, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, IDocumentStorePort>? DocumentStore { get; private set; }

    /// <summary>Gets the moderation vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<IModerationAdapter>? Moderation { get; private set; }

    /// <summary>Gets the telemetry vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<ITelemetryAdapter>? Telemetry { get; private set; }

    /// <summary>Gets the audit sink vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<IAuditSinkAdapter>? AuditSinks { get; private set; }

    /// <summary>Gets the speech vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<ISpeechAdapter>? Speech { get; private set; }

    /// <summary>Gets the call transports this host supports, or <see langword="null"/>.</summary>
    internal IReadOnlyList<ICallAdapter>? Call { get; private set; }

    /// <summary>Gets the extra tool factory links, in the order the composite asks them.</summary>
    internal IReadOnlyList<Func<AgentCoreStartup, IAgentToolFactory>> ToolFactories => _toolFactories;

    /// <summary>Gets the observers the host registered, in the order it registered them.</summary>
    internal IReadOnlyList<ICallObserver> Observers => _observers;

    /// <summary>Binds the vendor adapters, and the document picks one by each entry's <c>kind</c>.</summary>
    /// <param name="adapters">One adapter for each vendor this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// This is the config-driven overload. The host lists what it supports, once, and every
    /// <c>providers.llm[]</c> entry routes to the adapter its <c>kind</c> names. A kind no adapter
    /// serves fails the start, and the message names both sides. The adapters read
    /// <see cref="SecretResolver"/> for their credentials, so bind that first or in any order —
    /// nothing is read until <c>AddAgentCoreAsync</c> runs.
    /// </remarks>
    public AgentCoreOptions UseChatClients(params IChatClientAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        return UseChatClients(async (startup, cancellationToken) => await CompositeChatClientFactory
            .CreateAsync(startup.Configuration, SecretResolver, adapters, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Binds the adapter that turns a model reference into a chat client.</summary>
    /// <param name="chatClients">Builds the adapter from the loaded document, without a wait.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// This seam is required, through one of the three overloads. The compile table asks it for
    /// every agent and for the extractor, so a host that binds none has no model and
    /// <c>AddAgentCoreAsync</c> says so.
    /// </remarks>
    public AgentCoreOptions UseChatClients(Func<AgentCoreStartup, IChatClientFactory> chatClients)
    {
        ArgumentNullException.ThrowIfNull(chatClients);
        return UseChatClients((startup, _) => ValueTask.FromResult(chatClients(startup)));
    }

    /// <summary>Binds the adapter that turns a model reference into a chat client, with a wait.</summary>
    /// <param name="chatClients">Builds the adapter from the loaded document.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// A factory that reads a credential or opens a connection awaits here, and no thread blocks:
    /// <c>AddAgentCoreAsync</c> awaits this seam while the host starts. The token is the one the
    /// host passed to <c>AddAgentCoreAsync</c>, so a cancelled start cancels the build too.
    /// </remarks>
    public AgentCoreOptions UseChatClients(Func<AgentCoreStartup, CancellationToken, ValueTask<IChatClientFactory>> chatClients)
    {
        ArgumentNullException.ThrowIfNull(chatClients);
        ChatClients = chatClients;
        return this;
    }

    /// <summary>Binds the knowledge vendors, and the document picks one for each knowledge port.</summary>
    /// <param name="adapters">One adapter for each knowledge vendor this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// <para>
    /// This is the config-driven seam, and it is the knowledge mirror of
    /// <see cref="UseChatClients(IChatClientAdapter[])"/>. The host lists what it supports, once, and
    /// <c>providers.knowledge.search</c> and <c>providers.knowledge.documents</c> route each port to
    /// the adapter its <c>kind</c> names. Both fields default to <c>filesystem</c>, so a document
    /// with no <c>knowledge:</c> block still binds both ports. A kind no adapter serves, and a kind
    /// whose adapter does not serve the port that named it, fail the start, and the message names
    /// both sides. The adapters read <see cref="SecretResolver"/> for their credentials.
    /// </para>
    /// <para>
    /// <b>An explicit <see cref="UseKnowledgeRetrieval"/>, <see cref="UseDocumentStore"/>, or
    /// <see cref="UseKnowledge{TKnowledge}"/> call wins over this registry, for the port or ports it
    /// sets.</b> A host therefore replaces one half of a document-driven pair with an object of its
    /// own and leaves the other half to the document. <b>A port an explicit call sets is neither
    /// resolved nor built here:</b> the field that names it is not read, no adapter of that kind is
    /// looked for, and no vendor is opened. So an override costs no thrown-away build, and a
    /// document that names a kind this host does not register still starts when the explicit call
    /// was going to answer that port anyway.
    /// </para>
    /// </remarks>
    public AgentCoreOptions UseKnowledgeStores(params IKnowledgeStoreAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        KnowledgeStores = adapters;
        return this;
    }

    /// <summary>Binds one adapter that answers both halves of the knowledge base.</summary>
    /// <typeparam name="TKnowledge">The adapter type, which implements both knowledge ports.</typeparam>
    /// <param name="knowledge">Builds the adapter from the loaded document.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// <para>
    /// A host whose store both ranks and reads writes this one line, and the file store is such a
    /// store. A host with two adapters calls <see cref="UseKnowledgeRetrieval"/> and
    /// <see cref="UseDocumentStore"/> instead, which is what the Zilliz connector beside a file
    /// document store needs.
    /// </para>
    /// <para>
    /// The factory runs once and the one object it returns answers both ports, so a host never opens
    /// its store twice.
    /// </para>
    /// </remarks>
    public AgentCoreOptions UseKnowledge<TKnowledge>(Func<AgentCoreStartup, TKnowledge> knowledge)
        where TKnowledge : class, IKnowledgeRetrievalPort, IDocumentStorePort
    {
        ArgumentNullException.ThrowIfNull(knowledge);

        TKnowledge? built = null;
        TKnowledge Once(AgentCoreStartup startup) => built ??= knowledge(startup);

        KnowledgeRetrieval = Once;
        DocumentStore = Once;
        return this;
    }

    /// <summary>Binds the adapter <c>knowledge.search</c> ranks with.</summary>
    /// <param name="retrieval">Builds the adapter from the loaded document.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// A document that declares no <c>knowledge.search</c> tool needs none. A document that declares
    /// one and finds no adapter fails at startup, and the message names this port.
    /// </remarks>
    public AgentCoreOptions UseKnowledgeRetrieval(Func<AgentCoreStartup, IKnowledgeRetrievalPort> retrieval)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        KnowledgeRetrieval = retrieval;
        return this;
    }

    /// <summary>Binds the adapter <c>knowledge.read</c> opens.</summary>
    /// <param name="documents">Builds the adapter from the loaded document.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// A document that declares no <c>knowledge.read</c> tool needs none. A document that declares
    /// one and finds no adapter fails at startup, and the message names this port.
    /// </remarks>
    public AgentCoreOptions UseDocumentStore(Func<AgentCoreStartup, IDocumentStorePort> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        DocumentStore = documents;
        return this;
    }

    /// <summary>Binds the moderation vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// <para>
    /// The host lists the vendors it supports, once, exactly as the llm and knowledge seams above.
    /// <c>providers.moderation.kind</c> picks the adapter, so a document that changes vendors changes
    /// no code here. Registering a vendor costs nothing until a document names it: an adapter no
    /// field names is never asked to build anything, so a host still starts with no key.
    /// </para>
    /// <para>
    /// <b>A document that names no <c>providers.moderation</c> moderates nothing.</b> Every turn then
    /// reaches the model, and no turn is refused. A document that names a kind this host does not
    /// register fails the start, and the message names what the host does register.
    /// </para>
    /// <para>
    /// The evaluator joins <see cref="EvaluatorRegistry"/> under
    /// <see cref="PromptModerator.ModerationEvaluatorName"/>, so the offline golden set reaches the
    /// same object the turn loop does. D13 asks for exactly that: an evaluator is written once and
    /// used twice.
    /// </para>
    /// <para>
    /// The words the caller speaks are moderated BEFORE the model runs, and a flagged turn speaks
    /// <see cref="AgentCoreConfiguration.RefusalReply"/> instead of reaching the model. That is the
    /// owner's decision of 2026-08-13, and it departs from section 11 item 11, which asked for the
    /// agent's reply to be moderated and recorded.
    /// </para>
    /// </remarks>
    public AgentCoreOptions UseModeration(params IModerationAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Moderation = adapters;
        return this;
    }

    /// <summary>Binds the telemetry vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// <para>
    /// The host lists the vendors it supports, once, exactly as the three seams above.
    /// <c>providers.telemetry.kind</c> picks the adapter, so a document that changes collectors
    /// changes no code here. Registering a vendor costs nothing until a document names it: an adapter
    /// no field names is never asked to start anything, so a host still starts with no key.
    /// </para>
    /// <para>
    /// <b>A document that names no <c>providers.telemetry</c> exports nothing.</b> Spans and
    /// measurements are still written, where they cost almost nothing with no listener attached, and
    /// log lines go wherever <see cref="LoggerFactory"/> already sends them. A document that names a
    /// kind this host does not register fails the start, and the message names what the host does
    /// register.
    /// </para>
    /// <para>
    /// The session that starts is added to <see cref="LoggerFactory"/> as one more provider, so a
    /// deployment reading its console keeps reading its console and the same line also reaches the
    /// collector. It is registered in the container too, so the host flushes it on the way out.
    /// </para>
    /// </remarks>
    public AgentCoreOptions UseTelemetry(params ITelemetryAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Telemetry = adapters;
        return this;
    }

    /// <summary>Binds the audit sink vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// <para>
    /// The host lists the vendors it supports, once, exactly as the four seams above.
    /// <c>providers.audit.kind</c> picks the adapter, so a document that moves the chain of D23 from
    /// one store to another changes no code here. Registering a vendor costs nothing until a document
    /// names it: an adapter no field names is never asked to open anything, so a host still starts
    /// with no database and no key.
    /// </para>
    /// <para>
    /// <b>This seam has a default, and the other five do not.</b> A document that names no
    /// <c>providers.audit</c> gets <see cref="AuditSinkFactory.MemoryKind"/> — the in-process sink —
    /// rather than nothing at all, because the turn loop produces the events of D23 whether or not a
    /// store exists and every reading of a call is now unconditional. That default keeps a test and a
    /// first run working with no database, and it is not durable: the in-process list grows without a
    /// bound and dies with the process. A deployment names a durable vendor here.
    /// </para>
    /// <para>
    /// What an adapter returns is a <b>store</b> and never a queue. <c>AddAgentCoreAsync</c> wraps
    /// whatever this seam opens in the bounded channel and batching background writer of section 7, so
    /// the "never sit on the turn" rule of <see cref="IAuditSinkPort"/> is applied in one place and a
    /// vendor adapter is a plain writer with no buffering of its own.
    /// </para>
    /// </remarks>
    public AgentCoreOptions UseAuditSinks(params IAuditSinkAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        AuditSinks = adapters;
        return this;
    }

    /// <summary>Binds the host's own readings of a call, beside the three this library keeps.</summary>
    /// <param name="observers">
    /// What the host wants told about every call. Each one takes every fact, in the order the call
    /// produced it. An empty set is legal and registers nothing.
    /// </param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// <para>
    /// This is the socket behind <see cref="ICallObserver"/>. That port is the one seam between the
    /// turn loop and everything that watches it, and this method is how a host reaches it: the audit
    /// chain of D23, the counters of section 8.6, and the "log once" rows of section 8.7 are the
    /// library's own three, and a host adds a fourth without touching any of them.
    /// </para>
    /// <para>
    /// <b>It adds rather than replaces.</b> Calling it twice keeps both registrations, so a host
    /// composes its readings across whatever code configures the container. Nothing here replaces
    /// <see cref="UseAuditSinks"/>: that names the store the chain is written to, and this binds a
    /// reader of the same facts.
    /// </para>
    /// <para>
    /// The host's observers are offered every fact <b>after</b> the library's own, which is what keeps
    /// the counters and the log rows at the cost they had before any host bound anything. Order across
    /// observers is fixed only for that synchronous head; past its first await each observer runs on a
    /// queue of its own, so a slow one delays nothing but its own later facts.
    /// </para>
    /// <para>
    /// The contract of <see cref="ICallObserver"/> applies in full: an observer completes when it has
    /// ACCEPTED the fact and never when it has stored it, and one that awaits a database or an HTTP
    /// call inside <c>OnCallEventAsync</c> makes the turn loop pay for it. One that throws is logged
    /// once and forgotten, and it costs neither the turn nor any other observer.
    /// </para>
    /// <para>
    /// One instance serves every call, because a <c>CallEvent</c> names its own call. An observer that
    /// holds per-call state keys it by <c>CallEvent.CallId</c>.
    /// </para>
    /// </remarks>
    public AgentCoreOptions UseObservers(params ICallObserver[] observers)
    {
        ArgumentNullException.ThrowIfNull(observers);
        _observers.AddRange(observers);
        return this;
    }

    /// <summary>Binds the speech vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// <para>
    /// The host lists the vendors it supports, once, exactly as the four seams above, and
    /// <c>providers.speech.stt.kind</c> and <c>providers.speech.tts.kind</c> are the fields a
    /// document names them with, one per role — so changing vendors is a document edit and no code
    /// edit here. Registering a vendor costs nothing until a document names it: nothing is opened
    /// while the host starts, whatever the document says.
    /// </para>
    /// <para>
    /// <b>This method selects nothing.</b> It only puts the list in the container, beside the
    /// document, for whatever asks the selector later. <c>AddAgentCoreAsync</c> does read both
    /// speech roles, but only to check that each agrees with <c>providers.call.kind</c>
    /// when the call vendor carries text; it never picks a speech vendor out of this list.
    /// </para>
    /// <para>
    /// How the two blocks constrain each other — that <c>providers.call</c> and
    /// <c>providers.speech</c> are required of one another, and that a transport whose frames carry
    /// text must be named by both speech roles — is written in <see cref="UseCall"/>'s remarks.
    /// </para>
    /// </remarks>
    public AgentCoreOptions UseSpeech(params ISpeechAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Speech = adapters;
        return this;
    }

    /// <summary>Lists the vendors that may carry a call, so <c>providers.call.kind</c> can pick one.</summary>
    /// <param name="adapters">The transports this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// <para>
    /// Registering a vendor is what turns this seam on, exactly as it is for telemetry, knowledge,
    /// moderation, and speech. A host that calls this and a document that names a kind none of
    /// these serves fails the start; a host that never calls it is not asked what its document says.
    /// </para>
    /// <para>
    /// <b>Unlike <see cref="UseSpeech"/>, <c>AddAgentCoreAsync</c> does read the document for this
    /// seam.</b> It selects the transport <c>providers.call.kind</c> names and then checks that both
    /// speech roles can coexist with it, because a transport whose frames already
    /// carry text performs recognition and synthesis itself. That agreement is a fact about the
    /// document, so it holds or fails whether or not a route is ever mapped.
    /// </para>
    /// <para>
    /// <b>The two blocks are required of one another.</b> The schema writes
    /// <c>required: ["call", "speech"]</c> on the <c>providers</c> object, so a document that writes
    /// a <c>providers</c> section writes both; and a configuration that registered a transport here
    /// and is missing either block is refused while the host starts, with a pointer at the block that
    /// is missing. A transport whose frames carry text must further name the same vendor in both,
    /// which is what the pairing above checks.
    /// </para>
    /// </remarks>
    public AgentCoreOptions UseCall(params ICallAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Call = adapters;
        return this;
    }

    /// <summary>Adds one more link to the tool factory chain.</summary>
    /// <param name="toolFactory">Builds the link from the loaded document and the resolved secrets.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// The built-in link and the binding link are built here. Every other kind, such as
    /// <c>kind: http</c>, lives in an adapter assembly and joins the chain through this method.
    /// </remarks>
    public AgentCoreOptions AddToolFactory(Func<AgentCoreStartup, IAgentToolFactory> toolFactory)
    {
        ArgumentNullException.ThrowIfNull(toolFactory);
        _toolFactories.Add(toolFactory);
        return this;
    }

    /// <summary>Registers one host delegate behind a <c>binds:</c> name.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes, such as <c>CreateCase</c>.</param>
    /// <param name="binding">The delegate the tool calls.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <exception cref="ArgumentException">The name is already registered.</exception>
    public AgentCoreOptions Bind(string name, ToolBinding binding)
    {
        Bindings.Register(name, binding);
        return this;
    }
}

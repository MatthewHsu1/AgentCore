using AgentCore.Application.Configuration.Schema;
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

    /// <summary>Gets or sets the sink the audit chain of D23 is appended to.</summary>
    /// <remarks>
    /// A host that binds none still runs, and every event it produces is dropped. Section 7 names the
    /// adapter this holds in production: <c>Infrastructure/Audit/Postgres</c>, behind a bounded
    /// <c>Channel</c> and a background writer, so the append never sits on the turn.
    /// </remarks>
    public IAuditSinkPort? AuditSink { get; set; }

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

    /// <summary>Gets the seam <c>knowledge.search</c> ranks with, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, IKnowledgeRetrievalPort>? KnowledgeRetrieval { get; private set; }

    /// <summary>Gets the seam <c>knowledge.read</c> opens, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, IDocumentStorePort>? DocumentStore { get; private set; }

    /// <summary>Gets the extra tool factory links, in the order the composite asks them.</summary>
    internal IReadOnlyList<Func<AgentCoreStartup, IAgentToolFactory>> ToolFactories => _toolFactories;

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

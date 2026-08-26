using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools.Binding;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// Everything <c>AddAgentCore</c> needs that the document does not hold.
/// </summary>
public sealed class AgentCoreOptions
{
    private readonly List<Func<AgentCoreStartup, IToolSource>> _toolSources = [];
    private readonly List<ICallObserver> _observers = [];

    /// <summary>Gets the path of the configuration document, or <see langword="null"/>.</summary>
    public string? ConfigurationPath { get; set; }

    /// <summary>Gets or sets a document the host already loaded, or <see langword="null"/>.</summary>
    public AgentCoreConfiguration? Configuration { get; set; }

    /// <summary>Gets or sets the chain that reads a <c>${secret:name}</c> reference.</summary>
    public ISecretResolverPort? SecretResolver { get; set; }

    /// <summary>Gets or sets the clock this container runs on, or <see langword="null"/> for <see cref="System.TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>Gets or sets the factory the library takes its loggers from.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Gets the map from a <c>binds:</c> name to the host delegate behind it.</summary>
    public ToolBindingRegistry Bindings { get; } = new();

    /// <summary>Gets the seam that resolves a model reference, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, CancellationToken, ValueTask<IChatClientFactory>>? ChatClients { get; private set; }

    /// <summary>Gets the embedding vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<IEmbeddingGeneratorAdapter>? Embeddings { get; private set; }

    /// <summary>Gets the knowledge vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<IKnowledgeStoreAdapter>? KnowledgeStores { get; private set; }

    /// <summary>Gets the seam that beats the <c>providers.knowledge.kind</c> registry, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, IKnowledgeRetrievalPort>? KnowledgeRetrieval { get; private set; }

    /// <summary>Gets the moderation vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<IModerationAdapter>? Moderation { get; private set; }

    /// <summary>Gets the telemetry vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<ITelemetryAdapter>? Telemetry { get; private set; }

    /// <summary>Gets the audit sink vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<IAuditSinkAdapter>? AuditSinks { get; private set; }

    /// <summary>Gets the transcript store vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<ITranscriptStoreAdapter>? TranscriptStores { get; private set; }

    /// <summary>Gets the speech vendors the host registered, or <see langword="null"/>.</summary>
    internal IReadOnlyList<ISpeechAdapter>? Speech { get; private set; }

    /// <summary>Gets the call transports this host supports, or <see langword="null"/>.</summary>
    internal IReadOnlyList<ICallAdapter>? Call { get; private set; }

    /// <summary>Gets the extra tool sources, in the order the registry asks them.</summary>
    internal IReadOnlyList<Func<AgentCoreStartup, IToolSource>> ToolSources => _toolSources;

    /// <summary>Gets the observers the host registered, in the order it registered them.</summary>
    internal IReadOnlyList<ICallObserver> Observers => _observers;

    /// <summary>Binds the vendor adapters, and the document picks one by each entry's <c>kind</c>.</summary>
    /// <param name="adapters">One adapter for each vendor this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
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
    public AgentCoreOptions UseChatClients(Func<AgentCoreStartup, IChatClientFactory> chatClients)
    {
        ArgumentNullException.ThrowIfNull(chatClients);
        return UseChatClients((startup, _) => ValueTask.FromResult(chatClients(startup)));
    }

    /// <summary>Binds the adapter that turns a model reference into a chat client, with a wait.</summary>
    /// <param name="chatClients">Builds the adapter from the loaded document.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseChatClients(Func<AgentCoreStartup, CancellationToken, ValueTask<IChatClientFactory>> chatClients)
    {
        ArgumentNullException.ThrowIfNull(chatClients);
        ChatClients = chatClients;
        return this;
    }

    /// <summary>Binds the embedding vendors, and the document picks one by <c>providers.embeddings.kind</c>.</summary>
    /// <param name="adapters">One adapter for each embedding vendor this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseEmbeddings(params IEmbeddingGeneratorAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Embeddings = adapters;
        return this;
    }

    /// <summary>Binds the knowledge vendors, and the document picks one by <c>providers.knowledge.kind</c>.</summary>
    /// <param name="adapters">One adapter for each knowledge vendor this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseKnowledgeStores(params IKnowledgeStoreAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        KnowledgeStores = adapters;
        return this;
    }

    /// <summary>Binds the adapter that beats the <c>providers.knowledge.kind</c> registry.</summary>
    /// <param name="retrieval">Builds the adapter from the loaded document.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseKnowledgeRetrieval(Func<AgentCoreStartup, IKnowledgeRetrievalPort> retrieval)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        KnowledgeRetrieval = retrieval;
        return this;
    }

    /// <summary>Binds the moderation vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseModeration(params IModerationAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Moderation = adapters;
        return this;
    }

    /// <summary>Binds the telemetry vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseTelemetry(params ITelemetryAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Telemetry = adapters;
        return this;
    }

    /// <summary>Binds the audit sink vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseAuditSinks(params IAuditSinkAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        AuditSinks = adapters;
        return this;
    }

    /// <summary>Binds the transcript store vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseTranscriptStores(params ITranscriptStoreAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        TranscriptStores = adapters;
        return this;
    }

    /// <summary>Binds the host's own readings of a call, beside the three this library keeps.</summary>
    /// <param name="observers">
    /// What the host wants told about every call. Each one takes every fact, in the order the call
    /// produced it. An empty set is legal and registers nothing.
    /// </param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseObservers(params ICallObserver[] observers)
    {
        ArgumentNullException.ThrowIfNull(observers);
        _observers.AddRange(observers);
        return this;
    }

    /// <summary>Binds the speech vendors, and the document picks one by <c>kind</c>.</summary>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseSpeech(params ISpeechAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Speech = adapters;
        return this;
    }

    /// <summary>Lists the vendors that may carry a call, so <c>providers.call.kind</c> can pick one.</summary>
    /// <param name="adapters">The transports this host supports.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions UseCall(params ICallAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Call = adapters;
        return this;
    }

    /// <summary>Adds one tool source the registry asks at startup.</summary>
    /// <param name="toolSource">
    /// Builds the source from the loaded document. The composition root calls this once and keeps
    /// what it returns: when the source it builds implements <see cref="IAsyncDisposable"/> or
    /// <see cref="IDisposable"/>, the composition root closes it when the host stops. Do not return an
    /// instance the host still needs after that.
    /// </param>
    /// <returns>These options, so a host chains its calls.</returns>
    public AgentCoreOptions AddToolSource(Func<AgentCoreStartup, IToolSource> toolSource)
    {
        ArgumentNullException.ThrowIfNull(toolSource);
        _toolSources.Add(toolSource);
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

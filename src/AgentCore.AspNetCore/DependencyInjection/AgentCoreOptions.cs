using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;

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

    /// <summary>Gets or sets the clock the reserved <c>callDurationSeconds</c> slot reads.</summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>Gets the map from a <c>binds:</c> name to the host delegate behind it.</summary>
    /// <remarks>
    /// The document writes <c>kind: binding</c> with <c>binds: CreateCase</c> and knows nothing else.
    /// <see cref="Bind(string, ToolBinding)"/> is how the host completes that seam.
    /// </remarks>
    public ToolBindingRegistry Bindings { get; } = new();

    /// <summary>Gets the seam that resolves a model reference, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, IChatClientFactory>? ChatClients { get; private set; }

    /// <summary>Gets the seam the two built-in tools read, or <see langword="null"/>.</summary>
    internal Func<AgentCoreStartup, IKnowledgePort>? Knowledge { get; private set; }

    /// <summary>Gets the extra tool factory links, in the order the composite asks them.</summary>
    internal IReadOnlyList<Func<AgentCoreStartup, IAgentToolFactory>> ToolFactories => _toolFactories;

    /// <summary>Binds the adapter that turns a model reference into a chat client.</summary>
    /// <param name="chatClients">Builds the adapter from the loaded document.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// This seam is required. The compile table asks it for every agent and for the extractor, so a
    /// host that binds none has no model and <c>AddAgentCore</c> says so.
    /// </remarks>
    public AgentCoreOptions UseChatClients(Func<AgentCoreStartup, IChatClientFactory> chatClients)
    {
        ArgumentNullException.ThrowIfNull(chatClients);
        ChatClients = chatClients;
        return this;
    }

    /// <summary>Binds the knowledge base the <c>kind: builtin</c> tools read.</summary>
    /// <param name="knowledge">Builds the adapter from the loaded document.</param>
    /// <returns>These options, so a host chains its calls.</returns>
    /// <remarks>
    /// A document that declares no <c>kind: builtin</c> tool needs none. A document that declares one
    /// and finds no store fails at startup, because the composite serves no kind it holds no link for.
    /// </remarks>
    public AgentCoreOptions UseKnowledge(Func<AgentCoreStartup, IKnowledgePort> knowledge)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        Knowledge = knowledge;
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

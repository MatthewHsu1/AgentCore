using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Everything the compile table needs that the document does not hold.
/// </summary>
/// <remarks>
/// The document names a model, a tool, and a guard. This context binds each name to the thing that
/// runs it, so the compiler stays a table over the document and holds no adapter of its own.
/// </remarks>
public sealed class AgentCompilationContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="chatClients">The seam that resolves a model reference.</param>
    public AgentCompilationContext(IChatClientFactory chatClients)
    {
        ArgumentNullException.ThrowIfNull(chatClients);
        ChatClients = chatClients;
    }

    /// <summary>
    /// Gets the seam that resolves a model reference.
    /// </summary>
    public IChatClientFactory ChatClients { get; }

    /// <summary>
    /// Gets or sets the seam that builds a tool. An agent advertises no tool when this is null.
    /// </summary>
    public IAgentToolFactory? Tools { get; init; }

    /// <summary>
    /// Gets or sets the evaluator a guarded graph edge calls.
    /// </summary>
    /// <remarks>
    /// A <c>graph:</c> with a guarded edge needs both this and <see cref="StateSnapshot"/>. A
    /// <c>policy:</c> takes its evaluator when the machine is built, once for each call.
    /// </remarks>
    public IGuardEvaluator? Guards { get; init; }

    /// <summary>
    /// Gets or sets the moderator that reads what the caller said before the model runs.
    /// </summary>
    /// <remarks>
    /// R3: the caller's words are moderated and a flagged turn is refused, so the check sits on the
    /// agent a turn runs rather than in the turn loop. A host that moderates
    /// nothing leaves this null and every turn runs. The moderator holds no per-call state, so one
    /// instance serves every call under T44.
    /// </remarks>
    public PromptModerator? Moderation { get; init; }

    /// <summary>
    /// Gets or sets the backing store of store 1, or <see langword="null"/> for memory.
    /// </summary>
    /// <remarks>
    /// One store serves every call, and the compiled <c>AgentCoreChatHistoryProvider</c> that writes
    /// to it is a process singleton under R7. It stays internal because the port is internal: a host
    /// binds a store through the composition root and never through this type.
    /// </remarks>
    internal ICallMessageStore? MessageStore { get; init; }

    /// <summary>
    /// Gets or sets the source of the state a guarded graph edge reads.
    /// </summary>
    /// <remarks>
    /// The compiled graph is a process singleton under T44, so this source must not close over the
    /// state of one call. It answers the state of the call running on the current flow of execution.
    /// <c>CallStateScope.Snapshot</c> is that source, and <c>AddAgentCore</c> binds it.
    /// </remarks>
    public Func<IReadOnlyDictionary<string, JsonNode?>>? StateSnapshot { get; init; }
}

using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Skills;
using AgentCore.Application.Tools.Registry;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Everything the compile table needs that the document does not hold.
/// </summary>
public sealed class AgentCompilationContext
{
    /// <summary>
    /// Creates the context.
    /// </summary>
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
    public ToolRegistry? Tools { get; init; }

    /// <summary>
    /// Gets or sets the evaluator a guarded graph edge calls.
    /// </summary>
    public IGuardEvaluator? Guards { get; init; }

    /// <summary>
    /// Gets or sets the moderator that reads what the caller said before the model runs.
    /// </summary>
    public PromptModerator? Moderation { get; init; }

    /// <summary>
    /// Gets or sets the backing store of store 1, or <see langword="null"/> for memory.
    /// </summary>
    public ICallStore? CallStore { get; init; }

    /// <summary>
    /// Gets or sets the source of the state a guarded graph edge reads.
    /// </summary>
    public Func<IReadOnlyDictionary<string, JsonNode?>>? StateSnapshot { get; init; }

    /// <summary>
    /// Gets or sets the store every agent's <c>knowledge:</c> block reads through.
    /// </summary>
    public IKnowledgeRetrievalPort? Knowledge { get; init; }

    /// <summary>
    /// Gets or sets the skills every agent's <c>skills:</c> list is drawn from, or
    /// <see langword="null"/> when the host bound no skills folder.
    /// </summary>
    public SkillCatalog? Skills { get; init; }

    /// <summary>
    /// Gets or sets the wording each card's source label is written in.
    /// </summary>
    public IKnowledgeCitationFormatter? Citations { get; init; }

    /// <summary>
    /// Gets or sets where the compiled agents write their own diagnostics, or <see langword="null"/>.
    /// </summary>
    public ILoggerFactory? Loggers { get; init; }
}

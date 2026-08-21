using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

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
    public ITranscriptStore? TranscriptStore { get; init; }

    /// <summary>
    /// Gets or sets the source of the state a guarded graph edge reads.
    /// </summary>
    public Func<IReadOnlyDictionary<string, JsonNode?>>? StateSnapshot { get; init; }
}

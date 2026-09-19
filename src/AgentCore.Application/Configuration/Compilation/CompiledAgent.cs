using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Policy;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// One entry, compiled to the row that it selected.
/// </summary>
public sealed class CompiledAgent
{
    private readonly CompiledDocument _document;

    private readonly CompileTableRow _row;

    private readonly EntryBuild _build;

    private readonly CompiledAgentSet _agents;

    private readonly TurnLayers _layers;

    private readonly Dictionary<string, AIAgent> _turnByAgentId;

    /// <summary>Compiles one entry: its row's build, with the turn layers on every agent a turn runs.</summary>
    /// <param name="builder">Every part of the entry, complete.</param>
    internal CompiledAgent(CompiledAgentBuilder builder)
    {
        _document = builder.Document;
        EntryName = builder.EntryName;
        Policy = builder.Policy;
        _row = builder.Row;
        _build = builder.Entry;
        _agents = builder.Agents;
        _layers = builder.Layers;

        TurnAgent = _layers.Apply(_build.Agent);
        _turnByAgentId = new Dictionary<string, AIAgent>(StringComparer.Ordinal);

        foreach (var (id, agent) in _agents.Agents)
        {
            _turnByAgentId[id] = _layers.Apply(agent);
        }
    }

    /// <summary>
    /// Gets whether the row answers its runs out of store 1 on its own session, rather than the
    /// request messages.
    /// </summary>
    internal bool SessionCarriesHistory => _row.SessionCarriesHistory;

    /// <summary>Gets the document this agent was compiled from.</summary>
    public AgentCoreConfiguration Configuration => _document.Configuration;

    /// <summary>Gets the entry key this agent was compiled from. It is the agent's name.</summary>
    internal string EntryName { get; }

    /// <summary>Gets this entry's stage machine, or <see langword="null"/> when the entry holds none.</summary>
    public PolicyConfiguration? Policy { get; }

    /// <summary>Gets the resolved line the caller hears when a turn fails.</summary>
    public string FallbackReply => _layers.FallbackReply;

    /// <summary>Gets the resolved line the caller hears when the agent refuses to answer.</summary>
    internal string RefusalReply => _layers.RefusalReply;

    /// <summary>Gets the row of the compile table this entry selected.</summary>
    internal CompiledAgentShape Shape => _row.Shape;

    /// <summary>Gets the name of the entry.</summary>
    public string Name => EntryName;

    /// <summary>
    /// Gets the agent a turn runs.
    /// </summary>
    public AIAgent Agent => _build.Agent;

    /// <summary>
    /// Gets the agents whose reply the caller hears, or <see langword="null"/> for all of them.
    /// </summary>
    internal IReadOnlySet<string>? SpokenBy => _layers.SpokenBy;

    /// <summary>
    /// Gets store 1 of every conversation this agent answers.
    /// </summary>
    internal AgentCoreChatHistoryProvider History => _document.History;

    /// <summary>Gets the store this agent's conversations and every word of them are kept in.</summary>
    internal IConversationStore ConversationStore => _document.Conversations;
    
    /// <summary>
    /// Gets the union of every harness provider's state keys, over every agent this document
    /// compiled — never the history provider's key. Empty when the document names no harness
    /// switch. This is what a conversation's <c>Providers</c> keeps beside its stage and its slots.
    /// </summary>
    internal IReadOnlySet<string> HarnessStateKeys => _agents.HarnessStateKeys;

#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    /// <summary>
    /// Gets every background provider this document compiled, over every agent. The conversation releases
    /// each one's session when it ends, so children still running cannot outlive it.
    /// </summary>
    internal IReadOnlyList<BackgroundAgentsProvider> BackgroundProviders => _agents.BackgroundProviders;
#pragma warning restore MAAI001

    /// <summary>
    /// Gets the agent a turn runs, with the turn-disposition layers on it.
    /// </summary>
    internal AIAgent TurnAgent { get; }

    /// <summary>
    /// Gets every compiled agent, keyed by the <c>agents.items</c> id.
    /// </summary>
    public IReadOnlyDictionary<string, AIAgent> Agents => _agents.Agents;

    /// <summary>Gets the agent one stage names.</summary>
    /// <param name="stageId">The stage id.</param>
    /// <returns>The agent, or <see langword="null"/> when the stage names none.</returns>
    /// <exception cref="KeyNotFoundException">The stage is not declared.</exception>
    public AIAgent? ForStage(string stageId)
    {
        ArgumentNullException.ThrowIfNull(stageId);

        if (!_build.Stages.TryGetValue(stageId, out var agentId))
        {
            throw new KeyNotFoundException($"The stage '{stageId}' is not declared in policy.stages.");
        }

        return agentId.Length == 0 ? null : _agents.Agents[agentId];
    }

    /// <summary>Gets the agent one stage names, with the turn-disposition layers on it.</summary>
    /// <param name="stageId">The stage id.</param>
    /// <returns>The agent, or <see langword="null"/> when the stage names none.</returns>
    /// <exception cref="KeyNotFoundException">The stage is not declared.</exception>
    internal AIAgent? TurnAgentForStage(string stageId)
    {
        ArgumentNullException.ThrowIfNull(stageId);

        if (!_build.Stages.TryGetValue(stageId, out var agentId))
        {
            throw new KeyNotFoundException($"The stage '{stageId}' is not declared in policy.stages.");
        }

        return agentId.Length == 0 ? null : _turnByAgentId[agentId];
    }

    /// <summary>Builds one stage machine for one conversation.</summary>
    /// <param name="guards">The evaluator that runs each exit guard.</param>
    /// <returns>The machine, in the initial stage.</returns>
    public StagePolicy CreatePolicy(IGuardEvaluator guards)
    {
        if (Policy is not { } policy)
        {
            throw new InvalidOperationException(
                $"The entry '{EntryName}' declares no policy, so it has no stage machine.");
        }

        return new StagePolicy(policy, guards);
    }
}

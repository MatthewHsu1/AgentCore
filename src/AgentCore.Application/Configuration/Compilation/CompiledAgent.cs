using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Policy;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// One document, compiled to the row that it selected.
/// </summary>
public sealed class CompiledAgent
{
    private readonly Dictionary<string, AIAgent> _byAgentId;

    private readonly Dictionary<string, string> _agentIdByStage;

    private readonly Dictionary<string, AIAgent> _turnByAgentId;

    internal CompiledAgent(
        AgentCoreConfiguration configuration,
        CompileTableRow row,
        ICallStore calls,
        AIAgent entry,
        Dictionary<string, AIAgent> byAgentId,
        Dictionary<string, string> agentIdByStage,
        IReadOnlySet<string>? spokenBy,
        AgentCoreChatHistoryProvider history,
        Func<AIAgent, AIAgent> turnLayers)
    {
        Configuration = configuration;
        Shape = row.Shape;
        SessionCarriesHistory = row.SessionCarriesHistory;
        Agent = entry;
        CallStore = calls;
        SpokenBy = spokenBy;
        History = history;

        _byAgentId = byAgentId;
        _agentIdByStage = agentIdByStage;

        TurnAgent = turnLayers(entry);
        _turnByAgentId = new Dictionary<string, AIAgent>(StringComparer.Ordinal);

        foreach (var (id, agent) in byAgentId)
        {
            _turnByAgentId[id] = turnLayers(agent);
        }
    }

    /// <summary>
    /// Gets whether the row answers its runs out of store 1 on its own session, rather than the
    /// request messages.
    /// </summary>
    internal bool SessionCarriesHistory { get; }

    /// <summary>Gets the document this agent was compiled from.</summary>
    public AgentCoreConfiguration Configuration { get; }

    /// <summary>Gets the row of the compile table this document selected.</summary>
    public CompiledAgentShape Shape { get; }

    /// <summary>Gets the name of the document.</summary>
    public string Name => Configuration.Name;

    /// <summary>
    /// Gets the agent a turn runs.
    /// </summary>
    public AIAgent Agent { get; }

    /// <summary>
    /// Gets the agents whose reply the caller hears, or <see langword="null"/> for all of them.
    /// </summary>
    internal IReadOnlySet<string>? SpokenBy { get; }

    /// <summary>
    /// Gets store 1 of every call this agent answers.
    /// </summary>
    internal AgentCoreChatHistoryProvider History { get; }

    /// <summary>Gets the store this agent's calls and every word of them are kept in.</summary>
    internal ICallStore CallStore { get; }

    /// <summary>
    /// Gets the agent a turn runs, with the turn-disposition layers on it.
    /// </summary>
    internal AIAgent TurnAgent { get; }

    /// <summary>
    /// Gets every compiled agent, keyed by the <c>agents.items</c> id.
    /// </summary>
    public IReadOnlyDictionary<string, AIAgent> Agents => _byAgentId;

    /// <summary>Gets the agent one stage names.</summary>
    /// <param name="stageId">The stage id.</param>
    /// <returns>The agent, or <see langword="null"/> when the stage names none.</returns>
    /// <exception cref="KeyNotFoundException">The stage is not declared.</exception>
    public AIAgent? ForStage(string stageId)
    {
        ArgumentNullException.ThrowIfNull(stageId);

        if (!_agentIdByStage.TryGetValue(stageId, out var agentId))
        {
            throw new KeyNotFoundException($"The stage '{stageId}' is not declared in policy.stages.");
        }

        return agentId.Length == 0 ? null : _byAgentId[agentId];
    }

    /// <summary>Gets the agent one stage names, with the turn-disposition layers on it.</summary>
    /// <param name="stageId">The stage id.</param>
    /// <returns>The agent, or <see langword="null"/> when the stage names none.</returns>
    /// <exception cref="KeyNotFoundException">The stage is not declared.</exception>
    internal AIAgent? TurnAgentForStage(string stageId)
    {
        ArgumentNullException.ThrowIfNull(stageId);

        if (!_agentIdByStage.TryGetValue(stageId, out var agentId))
        {
            throw new KeyNotFoundException($"The stage '{stageId}' is not declared in policy.stages.");
        }

        return agentId.Length == 0 ? null : _turnByAgentId[agentId];
    }

    /// <summary>Builds one stage machine for one call.</summary>
    /// <param name="guards">The evaluator that runs each exit guard.</param>
    /// <returns>The machine, in the initial stage.</returns>
    public StagePolicy CreatePolicy(IGuardEvaluator guards)
    {
        if (Configuration.Policy is not { } policy)
        {
            throw new InvalidOperationException(
                $"The document '{Name}' declares no policy, so it has no stage machine.");
        }

        return new StagePolicy(policy, guards);
    }
}

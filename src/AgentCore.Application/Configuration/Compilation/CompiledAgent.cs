using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Policy;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// One document, compiled to the row of section 8.2 that it selected.
/// </summary>
/// <remarks>
/// <para>
/// T44 closed by measurement: a fan-out of 26 simultaneous runs is clean against one shared
/// <c>ChatClientAgent</c> and against one shared <c>AsAIAgent()</c> wrapper, and
/// <c>AIAgentBinding</c> reports <c>SupportsConcurrentSharedExecution = True</c>. The compiled agent
/// is therefore a process singleton, and no code path may compile one for each call. Register this
/// object once through <see cref="CompiledAgentRegistry"/> and share it.
/// </para>
/// <para>
/// The per-call state is not here. <see cref="CreatePolicy"/> builds one stage machine for each
/// call, because a machine holds the stage of one call and nothing else.
/// </para>
/// </remarks>
public sealed class CompiledAgent
{
    private readonly Dictionary<string, AIAgent> _byAgentId;
    private readonly Dictionary<string, string> _agentIdByStage;
    private readonly Dictionary<string, AIAgent> _turnByAgentId;

    internal CompiledAgent(
        AgentCoreConfiguration configuration,
        CompiledAgentShape shape,
        AIAgent entry,
        Dictionary<string, AIAgent> byAgentId,
        Dictionary<string, string> agentIdByStage,
        IReadOnlySet<string>? spokenBy,
        AgentCoreChatHistoryProvider history,
        Func<AIAgent, AIAgent> turnLayers)
    {
        Configuration = configuration;
        Shape = shape;
        Agent = entry;
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

    /// <summary>Gets the document this agent was compiled from.</summary>
    public AgentCoreConfiguration Configuration { get; }

    /// <summary>Gets the row of the compile table this document selected.</summary>
    public CompiledAgentShape Shape { get; }

    /// <summary>Gets the name of the document.</summary>
    public string Name => Configuration.Name;

    /// <summary>
    /// Gets the agent a turn runs.
    /// </summary>
    /// <remarks>
    /// For <see cref="CompiledAgentShape.SingleAgent"/> it is the one <c>ChatClientAgent</c>. For a
    /// graph row it is the workflow wrapped by <c>AsAIAgent()</c>. For
    /// <see cref="CompiledAgentShape.Policy"/> it is the agent of the initial stage, and
    /// <see cref="ForStage(string)"/> gives the agent of any other stage.
    /// </remarks>
    public AIAgent Agent { get; }

    /// <summary>Gets the agents whose reply the caller hears, or <see langword="null"/> for all of them.</summary>
    /// <remarks>
    /// A graph row runs several agents for one turn and the caller hears one of them. This names
    /// which, so the streaming seam hands the host the answer and not the deliberation that produced
    /// it. It is null on the rows that run one agent, and on the graph patterns where any participant
    /// may legitimately answer last.
    /// </remarks>
    internal IReadOnlySet<string>? SpokenBy { get; }

    /// <summary>Gets store 1 of every call this agent answers.</summary>
    /// <remarks>
    /// One instance is shared by every call under R7, and it holds nothing about any of them: the
    /// words of a call live in that call's <see cref="AgentSession"/>. <c>CallSession</c> hands it to
    /// each run so the framework serves the history from it, and writes the finished turn to it
    /// itself.
    /// </remarks>
    internal AgentCoreChatHistoryProvider History { get; }

    /// <summary>Gets the agent a turn runs, with the turn-disposition layers on it.</summary>
    /// <remarks>
    /// <see cref="Agent"/> is the bare compiled artifact, and a host that consumes it reads whatever
    /// the compile table promised — including the fault a graph that matched no edge raises. This one
    /// is what <c>CallSession</c> runs: R1, R2 and R3 turn every audible turn into a successful run,
    /// so the turn loop never branches on how a turn went.
    /// </remarks>
    internal AIAgent TurnAgent { get; }

    /// <summary>Gets every compiled agent, keyed by the <c>agents.items</c> id.</summary>
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
    /// <remarks>See <see cref="TurnAgent"/> for why a turn runs this one and not <see cref="ForStage"/>.</remarks>
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
    /// <exception cref="InvalidOperationException">The document declares no <c>policy:</c>.</exception>
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

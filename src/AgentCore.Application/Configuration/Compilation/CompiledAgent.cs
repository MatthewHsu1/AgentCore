using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Policy;
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

    internal CompiledAgent(
        AgentCoreConfiguration configuration,
        CompiledAgentShape shape,
        AIAgent entry,
        Dictionary<string, AIAgent> byAgentId,
        Dictionary<string, string> agentIdByStage)
    {
        Configuration = configuration;
        Shape = shape;
        Agent = entry;
        _byAgentId = byAgentId;
        _agentIdByStage = agentIdByStage;
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

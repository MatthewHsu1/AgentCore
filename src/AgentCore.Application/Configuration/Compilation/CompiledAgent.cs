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
    private readonly Dictionary<string, AIAgent> _byAgentId;

    private readonly Dictionary<string, string> _agentIdByStage;

    private readonly Dictionary<string, AIAgent> _turnByAgentId;

    /// <summary>Compiles one entry: its row's build, with the turn layers on every agent a turn runs.</summary>
    /// <param name="document">What every entry of the document shares.</param>
    /// <param name="entryName">The entry key. It is the agent's name.</param>
    /// <param name="policy">The entry's stage machine, or <see langword="null"/> when it holds none.</param>
    /// <param name="row">The row of the compile table the entry selected.</param>
    /// <param name="build">What the row built for the entry.</param>
    /// <param name="agents">The <c>agents.items</c> entries the row built on.</param>
    /// <param name="layers">The turn layers of the entry.</param>
    internal CompiledAgent(
        CompiledDocument document,
        string entryName,
        PolicyConfiguration? policy,
        CompileTableRow row,
        EntryBuild build,
        CompiledAgentSet agents,
        TurnLayers layers)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryName);

        Configuration = document.Configuration;
        EntryName = entryName;
        Policy = policy;
        FallbackReply = layers.FallbackReply;
        RefusalReply = layers.RefusalReply;
        Shape = row.Shape;
        SessionCarriesHistory = row.SessionCarriesHistory;
        Agent = build.Agent;
        ConversationStore = document.Conversations;
        SpokenBy = layers.SpokenBy;
        History = document.History;
        HarnessStateKeys = agents.HarnessStateKeys;
        BackgroundProviders = agents.BackgroundProviders;
        _byAgentId = agents.Agents;
        _agentIdByStage = build.Stages;

        TurnAgent = layers.Apply(build.Agent);
        _turnByAgentId = new Dictionary<string, AIAgent>(StringComparer.Ordinal);

        foreach (var (id, agent) in agents.Agents)
        {
            _turnByAgentId[id] = layers.Apply(agent);
        }
    }

    /// <summary>
    /// Gets whether the row answers its runs out of store 1 on its own session, rather than the
    /// request messages.
    /// </summary>
    internal bool SessionCarriesHistory { get; }

    /// <summary>Gets the document this agent was compiled from.</summary>
    public AgentCoreConfiguration Configuration { get; }

    /// <summary>Gets the entry key this agent was compiled from. It is the agent's name.</summary>
    public string EntryName { get; }

    /// <summary>Gets this entry's stage machine, or <see langword="null"/> when the entry holds none.</summary>
    public PolicyConfiguration? Policy { get; }

    /// <summary>Gets the resolved line the caller hears when a turn fails.</summary>
    public string FallbackReply { get; }

    /// <summary>Gets the resolved line the caller hears when the agent refuses to answer.</summary>
    public string RefusalReply { get; }

    /// <summary>Gets the row of the compile table this entry selected.</summary>
    public CompiledAgentShape Shape { get; }

    /// <summary>Gets the name of the entry.</summary>
    public string Name => EntryName;

    /// <summary>
    /// Gets the agent a turn runs.
    /// </summary>
    public AIAgent Agent { get; }

    /// <summary>
    /// Gets the agents whose reply the caller hears, or <see langword="null"/> for all of them.
    /// </summary>
    internal IReadOnlySet<string>? SpokenBy { get; }

    /// <summary>
    /// Gets store 1 of every conversation this agent answers.
    /// </summary>
    internal AgentCoreChatHistoryProvider History { get; }

    /// <summary>Gets the store this agent's conversations and every word of them are kept in.</summary>
    internal IConversationStore ConversationStore { get; }
    /// <summary>
    /// Gets the union of every harness provider's state keys, over every agent this document
    /// compiled — never the history provider's key. Empty when the document names no harness
    /// switch. This is what a conversation's <c>Providers</c> keeps beside its stage and its slots.
    /// </summary>
    internal IReadOnlySet<string> HarnessStateKeys { get; }

#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    /// <summary>
    /// Gets every background provider this document compiled, over every agent. The conversation releases
    /// each one's session when it ends, so children still running cannot outlive it.
    /// </summary>
    internal IReadOnlyList<BackgroundAgentsProvider> BackgroundProviders { get; }
#pragma warning restore MAAI001

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

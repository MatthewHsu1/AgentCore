using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Row 2: <c>agents:</c> plus <c>policy:</c>. The machine picks a stage each turn, the stage names
/// one agent, and that agent's run answers the caller. Runtime is <c>Stateless</c>.
/// </summary>
internal sealed class PolicyRow : CompileTableRow
{
    internal static readonly PolicyRow Instance = new();

    /// <summary>A stage that names no agent. CompiledAgent.ForStage reads the same sentinel.</summary>
    private const string NoAgentId = "";

    internal override CompiledAgentShape Shape => CompiledAgentShape.Policy;

    internal override bool SessionCarriesHistory => true;

    internal override (AIAgent Entry, Dictionary<string, string> Stages) BuildEntry(
        AgentCoreConfiguration configuration,
        Dictionary<string, AIAgent> agents,
        AgentCompilationContext context)
    {
        var policy = configuration.Policy!;
        Dictionary<string, string> stages = new(StringComparer.Ordinal);

        for (var index = 0; index < policy.Stages.Count; index++)
        {
            var stage = policy.Stages[index];
            if (stage.Agent is not { } agentId)
            {
                stages[stage.Id] = NoAgentId;
                continue;
            }

            if (!agents.ContainsKey(agentId))
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(
                        ConfigurationError.AppendPointer("/policy/stages", index), "agent"),
                    $"the stage '{stage.Id}' names the agent '{agentId}', which agents.items does not declare.");
            }

            stages[stage.Id] = agentId;
        }

        if (!stages.TryGetValue(policy.Initial, out var initialAgent))
        {
            throw ConfigurationCompiler.Fail(
                "/policy/initial",
                $"the initial stage '{policy.Initial}' is not declared in policy.stages.");
        }

        if (initialAgent.Length == 0)
        {
            throw ConfigurationCompiler.Fail(
                "/policy/initial",
                $"the initial stage '{policy.Initial}' names no agent, so no turn can run.");
        }

        return (agents[initialAgent], stages);
    }
}

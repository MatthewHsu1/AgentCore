using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Row 1: the entry holds <c>agent:</c>. The named <c>agents.items</c> entry is the
/// entry, and its run's own last message is the reply.
/// </summary>
internal sealed class SingleAgentRow : CompileTableRow
{
    internal static readonly SingleAgentRow Instance = new();

    internal override CompiledAgentShape Shape => CompiledAgentShape.SingleAgent;

    internal override bool SessionCarriesHistory => true;

    internal override (AIAgent Entry, Dictionary<string, string> Stages) BuildEntry(
        AgentCoreConfiguration configuration,
        string entryName,
        EntryConfiguration entry,
        string entryPointer,
        Dictionary<string, AIAgent> agents,
        AgentCompilationContext context)
    {
        var agentPointer = ConfigurationError.AppendPointer(entryPointer, "agent");

        if (entry.Agent is not { Length: > 0 } agentId)
        {
            throw ConfigurationCompiler.Fail(
                agentPointer,
                $"the entry '{entryName}' names no agent, so nothing runs.");
        }

        if (!agents.TryGetValue(agentId, out var agent))
        {
            throw ConfigurationCompiler.Fail(
                agentPointer,
                $"the entry '{entryName}' names the agent '{agentId}', which agents.items does not declare.");
        }

        return (agent, NoStages());
    }
}

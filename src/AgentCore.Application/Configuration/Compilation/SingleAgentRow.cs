using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>
    /// Row 1: the entry holds <c>agent:</c>. The named <c>agents.items</c> entry is the
    /// entry, and its run's own last message is the reply.
    /// </summary>
    internal sealed class SingleAgentRow : CompileTableRow
    {
        internal static readonly SingleAgentRow Instance = new();

        internal override CompiledAgentShape Shape => CompiledAgentShape.SingleAgent;

        internal override bool SessionCarriesHistory => true;

        internal override EntryBuild BuildEntry(
            AgentCoreConfiguration configuration,
            string entryName,
            EntryConfiguration entry,
            string entryPointer,
            Dictionary<string, AIAgent> agents,
            AgentCompilationContext context)
        {
            string agentPointer = ConfigurationError.AppendPointer(entryPointer, "agent");

            return entry.Agent switch
            {
                not { Length: > 0 } => throw ConfigurationCompiler.Fail(
                    agentPointer,
                    $"the entry '{entryName}' names no agent, so nothing runs."),
                string agentId when agents.TryGetValue(agentId, out AIAgent? agent) => new EntryBuild(agent, NoStages()),
                string agentId => throw ConfigurationCompiler.Fail(
                    agentPointer,
                    $"the entry '{entryName}' names the agent '{agentId}', which agents.items does not declare."),
            };
        }
    }
}

using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Row 1: one <c>agents.items</c> entry, no <c>policy:</c>, no <c>graph:</c>. The one agent is the
/// entry, and its run's own last message is the reply.
/// </summary>
internal sealed class SingleAgentRow : CompileTableRow
{
    internal static readonly SingleAgentRow Instance = new();

    internal override CompiledAgentShape Shape => CompiledAgentShape.SingleAgent;

    internal override bool SessionCarriesHistory => true;

    internal override (AIAgent Entry, Dictionary<string, string> Stages) BuildEntry(
        AgentCoreConfiguration configuration,
        Dictionary<string, AIAgent> agents,
        AgentCompilationContext context)
        => (agents[configuration.Agents!.Items[0].Id], NoStages());
}

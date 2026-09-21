using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>What one compile-table row built for one entry.</summary>
    /// <param name="Agent">The agent a turn runs.</param>
    /// <param name="Stages">The agent id each <c>policy.stages</c> entry names. Empty for a row with no stages.</param>
    internal sealed record EntryBuild(AIAgent Agent, Dictionary<string, string> Stages);
}

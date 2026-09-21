using Microsoft.Agents.AI.Compaction;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// What the two compaction providers of an agent are built from. They run one after the other in
    /// the agent's provider chain: the cap first, so the summary reads short results and counts them.
    /// </summary>
    /// <param name="Cap">Cuts old tool results. No model call.</param>
    /// <param name="Summary">The strategy <see cref="SummaryRowProvider"/> runs. One model call, its output stored as a row.</param>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal sealed record CompactionStages(ToolResultCapProvider Cap, CompactionStrategy Summary);
#pragma warning restore MAAI001
}

using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// What the two compaction providers of an agent are built from. They run one after the other in
    /// the agent's provider chain: the cap first, so the summary reads short results and counts them.
    /// </summary>
    /// <param name="Cap">Cuts old tool results. No model call.</param>
    /// <param name="Summariser">The chat client the summary stage calls.</param>
    /// <param name="Summary">
    /// Builds the strategy <see cref="SummaryRowProvider"/> runs, over a client it wraps itself. One
    /// call per turn: a <see cref="SummarizationCompactionStrategy"/> holds only its config, so
    /// building a fresh one costs nothing worth caching.
    /// </param>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal sealed record CompactionStages(ToolResultCapProvider Cap, IChatClient Summariser, Func<IChatClient, CompactionStrategy> Summary);
#pragma warning restore MAAI001
}

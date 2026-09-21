using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation
{
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    /// <summary>The <c>agents.items</c> entries one build produced, and what every one of them declared.</summary>
    /// <param name="Agents">The compiled agents, keyed by id.</param>
    /// <param name="HarnessStateKeys">The union of every harness provider's state keys. Never the history provider's key.</param>
    /// <param name="BackgroundProviders">Every background provider, for the conversation-end release.</param>
    internal sealed record CompiledAgentSet(
        Dictionary<string, AIAgent> Agents,
        IReadOnlySet<string> HarnessStateKeys,
        IReadOnlyList<BackgroundAgentsProvider> BackgroundProviders);
#pragma warning restore MAAI001
}

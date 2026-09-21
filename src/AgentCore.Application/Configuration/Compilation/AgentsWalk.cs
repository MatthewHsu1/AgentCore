using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation
{
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    /// <summary>What one walk over <c>agents.items</c> shares between every agent it compiles.</summary>
    /// <param name="Defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="Clarification">The document's ambiguity wiring (§8), built once rather than re-derived per agent.</param>
    /// <param name="Scope">The <c>providers.knowledge.scope</c> block, or <see langword="null"/>.</param>
    /// <param name="Background">Collects the built background providers, so the conversation can release their sessions when it ends.</param>
    internal sealed record AgentsWalk(
        AgentDefaults? Defaults,
        ResolvedClarification Clarification,
        KnowledgeScopeConfiguration? Scope,
        ICollection<BackgroundAgentsProvider> Background);
#pragma warning restore MAAI001
}

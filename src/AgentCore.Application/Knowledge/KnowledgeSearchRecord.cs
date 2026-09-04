using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Reads one finished store call into the record an operator debugs an outage from.
/// </summary>
internal static class KnowledgeSearchRecord
{
    /// <summary>Builds the record.</summary>
    /// <param name="agent">The id of the agent that asked.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="query">The search text the framework composed.</param>
    /// <param name="cards">What the store returned, or empty when it threw.</param>
    /// <param name="latencyMs">How long that one store call took, in milliseconds.</param>
    /// <param name="failure">What the port threw, or <see langword="null"/> when it answered.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    /// The scope comes from the live ambient, so a caller has to build this while the scope the call
    /// ran under is still open. The probe's second search opens a narrowed scope of its own, and its
    /// record is meant to name that one rather than the scope the main search used.
    /// </remarks>
    internal static KnowledgeAuditRecord Of(
        string agent,
        ResolvedKnowledge knowledge,
        string query,
        IReadOnlyList<KnowledgeCard> cards,
        double latencyMs,
        Exception? failure)
        => KnowledgeAuditRecord.For(
            turnId: null,
            agent,
            knowledge.Mode,
            query,
            KnowledgeScopeScope.Current,
            cards,
            latencyMs,
            failure);
}

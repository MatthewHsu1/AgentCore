using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Knowledge
{
    /// <summary>What one agent's knowledge search is bound to, once, at compile time.</summary>
    /// <param name="Port">The store every search reads.</param>
    /// <param name="Knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="Agent">The id of the agent that asks, for the log line.</param>
    /// <param name="Citations">The wording <c>providers.knowledge.citation</c> named.</param>
    /// <param name="Logger">Where the search's own log events go.</param>
    internal sealed record KnowledgeBinding(
        IKnowledgeRetrievalPort Port,
        ResolvedKnowledge Knowledge,
        string Agent,
        IKnowledgeCitationFormatter Citations,
        ILogger Logger);
}

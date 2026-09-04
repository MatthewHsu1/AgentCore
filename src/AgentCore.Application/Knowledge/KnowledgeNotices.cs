using Microsoft.Agents.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// What a knowledge search puts in front of the model when it has no cards to show.
/// </summary>
internal static class KnowledgeNotices
{
    /// <summary>The source name every notice carries, so the model can never cite one as a card.</summary>
    internal const string SourceName = "agentcore:notice";

    /// <summary>What the model is told when the search itself failed.</summary>
    internal const string Unreachable =
        "The knowledge base is unreachable for this turn. Say so, and do not answer from memory.";

    /// <summary>What the model is told when this agent is scoped and the turn has no scope open.</summary>
    internal const string NoScope =
        "The knowledge base was not searched for this turn, because no scope is open to search "
        + "within. Say you cannot look this up, and do not answer from memory.";

    /// <summary>What the model is told when the search ran and matched nothing.</summary>
    /// <remarks>
    /// Tool mode only. Prefetch searches before every invocation on a query composed from recent
    /// messages, so a greeting would otherwise open the call by announcing an empty knowledge base.
    /// </remarks>
    internal const string Empty =
        "The knowledge base holds nothing for this question. Say so, and do not answer from memory.";

    /// <summary>Wraps one sentence for the model in the shape the framework injects.</summary>
    /// <param name="text">What the model is told.</param>
    /// <returns>The result.</returns>
    internal static TextSearchProvider.TextSearchResult Of(string text)
        => new() { Text = text, SourceName = SourceName };
}

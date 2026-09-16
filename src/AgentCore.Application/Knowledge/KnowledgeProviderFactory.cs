using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Binds one agent's resolved <c>knowledge:</c> block to the framework's retrieval seam.
/// </summary>
internal static class KnowledgeProviderFactory
{

    /// <summary>Builds the provider one agent binds.</summary>
    /// <param name="port">The store every agent shares.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="agent">The id of the agent this provider hangs on, for the log line.</param>
    /// <param name="citations">The wording <c>providers.knowledge.citation</c> named.</param>
    /// <param name="loggers">
    /// Where the retrieval record and the framework's own provider log go, or <see langword="null"/>
    /// when the host wired none. Ruling 21: this is the one reachable observability seam — the audit
    /// sink is not, because <c>AuditEvent</c> requires a call id and a sequence number that this
    /// seam doesn't carry down here.
    /// </param>
    /// <param name="scope">The document's <c>providers.knowledge.scope</c> block, or <see langword="null"/>.</param>
    /// <returns>The provider to hang on that agent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    internal static AIContextProvider Create(
        IKnowledgeRetrievalPort port,
        ResolvedKnowledge knowledge,
        string agent,
        IKnowledgeCitationFormatter citations,
        ILoggerFactory? loggers,
        KnowledgeScopeConfiguration? scope = null)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(citations);

        var logger = loggers?.CreateLogger(typeof(KnowledgeProviderFactory)) ?? NullLogger.Instance;

        TextSearchProviderOptions options = new()
        {
            SearchTime = knowledge.Mode == KnowledgeMode.Tool
                ? TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling
                : TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,

            CitationsPrompt = knowledge.Citations
                ? "Name the source document when you use it. Do not invent a link."
                : string.Empty,

            RecentMessageMemoryLimit = 4,
        };

        KnowledgeSearch.Core core = KnowledgeSearch.Bind(port, knowledge, agent, citations, logger);

        // The template's own delegate never runs: tool-mode searches go through the
        // invocation-bound wrapper, prefetch through per-invocation providers. It fails
        // loudly if either path ever leaks through.
        AIContextProvider template = new TextSearchProvider(UnreachableSearch, options, loggers);

        if (knowledge.Mode != KnowledgeMode.Tool)
        {
            return new KnowledgePrefetchProvider(template, options, loggers, core);
        }

        return new FacetFilterProvider(template, scope?.Filterable, core);
    }

    private static Task<IEnumerable<TextSearchProvider.TextSearchResult>> UnreachableSearch(
        string query, CancellationToken cancellationToken)
        => Task.FromException<IEnumerable<TextSearchProvider.TextSearchResult>>(
            new InvalidOperationException(
                "A knowledge search ran outside its turn. Searches run through the turn's own provider or tool."));
}

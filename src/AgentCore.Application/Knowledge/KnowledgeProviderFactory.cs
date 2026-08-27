using System.Diagnostics;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Binds one agent's resolved <c>knowledge:</c> block to the framework's retrieval seam.
/// </summary>
internal static class KnowledgeProviderFactory
{
    /// <summary>
    /// What the model is told when the search itself failed.
    /// </summary>
    private const string UnreachableNotice =
        "The knowledge base is unreachable for this turn. Say so, and do not answer from memory.";

    /// <summary>
    /// What the model is told when this agent is scoped and the turn has no scope open.
    /// </summary>
    private const string NoScopeNotice =
        "The knowledge base was not searched for this turn, because no scope is open to search "
        + "within. Say you cannot look this up, and do not answer from memory.";

    /// <summary>
    /// The scope an agent that declares <c>scoped: false</c> searches under.
    /// </summary>
    private static readonly KnowledgeScope WholeCorpus =
        new() { Facets = new Dictionary<string, string>(StringComparer.Ordinal) };

    /// <summary>Builds the provider one agent binds.</summary>
    /// <param name="port">The store every agent shares.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="agent">The id of the agent this provider hangs on, for the log line.</param>
    /// <param name="loggers">
    /// Where the retrieval record and the framework's own provider log go, or <see langword="null"/>
    /// when the host wired none. Ruling 21: this is the one reachable observability seam — the audit
    /// sink is not, because <c>AuditEvent</c> requires a call id and a sequence number that no
    /// ambient carries down here.
    /// </param>
    /// <returns>The provider to hang on that agent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    internal static AIContextProvider Create(
        IKnowledgeRetrievalPort port,
        ResolvedKnowledge knowledge,
        string agent,
        ILoggerFactory? loggers)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(agent);

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

        return new TextSearchProvider(SearchAsync, options, loggers);

        async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchAsync(
            string query, CancellationToken cancellationToken)
        {
            // An ambient with no facets filters nothing, so it is the absent ambient in disguise.
            // The shared store can only fail closed when EVERY agent is scoped, so in a mixed
            // deployment this is the only check standing between a scoped agent and every
            // customer's cards.
            if (knowledge.Scoped && KnowledgeScopeScope.Current is not { Facets.Count: > 0 })
            {
                return [Notice(NoScopeNotice)];
            }

            using var whole = knowledge.Scoped ? null : KnowledgeScopeScope.Open(WholeCorpus);

            var started = Stopwatch.GetTimestamp();

            try
            {
                var cards = await port.SearchAsync(query, cancellationToken).ConfigureAwait(false);

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var record = Record(agent, knowledge, query, cards, started, failure: null).ForLog();
                    Log.KnowledgeRetrieved(logger, agent, cards.Count, record);
                }

                return Trim(cards, knowledge);
            }
            catch (Exception failure) when (!CallerCancelled(failure, cancellationToken))
            {
                var record = Record(agent, knowledge, query, [], started, failure).ForLog();
                Log.KnowledgeRetrievalFailed(logger, agent, record, failure);

                return [Notice(UnreachableNotice)];
            }
        }
    }

    /// <summary>Whether a failure is the caller ending the turn, rather than the retrieval failing.</summary>
    private static bool CallerCancelled(Exception failure, CancellationToken cancellationToken)
        => failure is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>Reads one finished retrieval into the record an operator debugs an outage from.</summary>
    /// <param name="agent">The id of the agent that asked.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="query">The search text the framework composed.</param>
    /// <param name="cards">What the store returned, or empty when it threw.</param>
    /// <param name="started">The timestamp taken immediately before the port call.</param>
    /// <param name="failure">What the port threw, or <see langword="null"/> when it answered.</param>
    /// <returns>The record.</returns>
    private static KnowledgeAuditRecord Record(
        string agent,
        ResolvedKnowledge knowledge,
        string query,
        IReadOnlyList<KnowledgeCard> cards,
        long started,
        Exception? failure)
        => KnowledgeAuditRecord.For(
            turnId: null,
            agent,
            knowledge.Mode,
            query,
            KnowledgeScopeScope.Current,
            cards,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            failure);

    /// <summary>Cuts one search down to the agent's <c>limit:</c>.</summary>
    /// <param name="cards">What the store returned, best first, links last.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <returns>The cards this agent sees.</returns>
    private static List<TextSearchProvider.TextSearchResult> Trim(
        IReadOnlyList<KnowledgeCard> cards, ResolvedKnowledge knowledge)
    {
        List<TextSearchProvider.TextSearchResult> kept = [];
        var ranked = 0;

        foreach (var card in cards)
        {
            if (!card.ViaLink && ranked++ >= knowledge.Limit)
            {
                continue;
            }

            kept.Add(KnowledgeCardMapper.ToResult(card, knowledge.Citations));
        }

        return kept;
    }

    /// <summary>Wraps one sentence for the model in the shape the framework injects.</summary>
    /// <param name="text">What the model is told.</param>
    /// <returns>The result.</returns>
    private static TextSearchProvider.TextSearchResult Notice(string text)
        => new() { Text = text };
}

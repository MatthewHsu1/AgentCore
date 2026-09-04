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
    /// The scope an agent that declares <c>scoped: false</c> searches under.
    /// </summary>
    private static readonly KnowledgeScope WholeCorpus =
        new() { Facets = new Dictionary<string, string>(StringComparer.Ordinal) };

    /// <summary>Builds the provider one agent binds.</summary>
    /// <param name="port">The store every agent shares.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="agent">The id of the agent this provider hangs on, for the log line.</param>
    /// <param name="citations">The wording <c>providers.knowledge.citation</c> named.</param>
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
        IKnowledgeCitationFormatter citations,
        ILoggerFactory? loggers)
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
                return [KnowledgeNotices.Of(KnowledgeNotices.NoScope)];
            }

            using var whole = knowledge.Scoped ? null : KnowledgeScopeScope.Open(WholeCorpus);

            var started = Stopwatch.GetTimestamp();

            // Held from the moment the main search returns, so a failure raised by anything after it —
            // the probe's own second search included — is still recorded against the time the main
            // search took, rather than against however long the probe went on to run.
            double? searched = null;

            try
            {
                var cards = await port.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                searched = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var record = KnowledgeSearchRecord
                        .Of(agent, knowledge, query, cards, searched.Value, failure: null)
                        .ForLog();

                    Log.KnowledgeRetrieved(logger, agent, cards.Count, record);
                }

                // §8 step 1-2: the probe runs only for a tool-mode search that cleared no card at
                // all (K13) and only for a scoped agent — an unscoped agent opened WholeCorpus, which
                // holds no facets, and its empty search stays an empty list, exactly as today (K19).
                if (cards.Count == 0
                    && knowledge.Mode == KnowledgeMode.Tool
                    && KnowledgeScopeScope.Current is { Facets.Count: > 0 } scope)
                {
                    return await KnowledgeProbe
                        .RunAsync(port, knowledge, scope, agent, query, logger, cancellationToken)
                        .ConfigureAwait(false);
                }

                var shown = Kept(cards, knowledge);
                Cite(shown, knowledge, citations);

                return Map(shown, knowledge, citations);
            }
            catch (Exception failure) when (!KnowledgeCancellation.ByCaller(failure, cancellationToken))
            {
                var latency = searched ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var record = KnowledgeSearchRecord
                    .Of(agent, knowledge, query, [], latency, failure)
                    .ForLog();

                Log.KnowledgeRetrievalFailed(logger, agent, record, failure);

                return [KnowledgeNotices.Of(KnowledgeNotices.Unreachable)];
            }
        }
    }

    /// <summary>Cites what this search read, for the caller's screen.</summary>
    /// <param name="cards">The cards the agent is actually shown, after <see cref="Kept"/> cuts the search down to the agent's <c>limit:</c> — not everything the store returned.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="citations">The wording <c>providers.knowledge.citation</c> named.</param>
    private static void Cite(
        IReadOnlyList<KnowledgeCard> cards,
        ResolvedKnowledge knowledge,
        IKnowledgeCitationFormatter citations)
    {
        if (!knowledge.Citations || CallSourceScope.Current is not { } port)
        {
            return;
        }

        foreach (var card in cards)
        {
            if (KnowledgeSourceMapper.ToSource(card, citations) is { } source)
            {
                port.Publish(source);
            }
        }
    }

    /// <summary>Cuts one search down to the agent's <c>limit:</c>.</summary>
    /// <param name="cards">What the store returned, best first, links last.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <returns>The cards this agent is shown.</returns>
    private static List<KnowledgeCard> Kept(IReadOnlyList<KnowledgeCard> cards, ResolvedKnowledge knowledge)
    {
        List<KnowledgeCard> kept = [];
        var ranked = 0;

        foreach (var card in cards)
        {
            if (!card.ViaLink && ranked++ >= knowledge.Limit)
            {
                continue;
            }

            kept.Add(card);
        }

        return kept;
    }

    /// <summary>Maps the cards this agent is shown into what the framework injects.</summary>
    /// <param name="cards">The cards <see cref="Kept"/> already cut down to the agent's <c>limit:</c>.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="citations">The wording each card's source label is written in.</param>
    /// <returns>The results the framework injects.</returns>
    private static List<TextSearchProvider.TextSearchResult> Map(
        IReadOnlyList<KnowledgeCard> cards,
        ResolvedKnowledge knowledge,
        IKnowledgeCitationFormatter citations)
    {
        List<TextSearchProvider.TextSearchResult> mapped = [];

        foreach (var card in cards)
        {
            mapped.Add(KnowledgeCardMapper.ToResult(card, knowledge.Citations, citations));
        }

        return mapped;
    }
}

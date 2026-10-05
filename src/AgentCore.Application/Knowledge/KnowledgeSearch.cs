using System.Diagnostics;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Knowledge
{
    // One search against the store with everything the turn hands it, as explicit values: the
    // scope to read under, the tool-call facets to overlay, and the turn's own probe, history
    // flag and citation port. The framework invokes the search in two shapes — a tool call
    // carrying arguments, and a prefetch carrying only a session — and both bind those shapes
    // to these values before calling here, so this core never reads the flow.
    internal static class KnowledgeSearch
    {
        /// <summary>Searches with an explicit scope, facets and turn.</summary>
        internal delegate Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> Core(
            string query,
            IReadOnlyDictionary<string, string>? facets,
            TurnInvocation? turn,
            CancellationToken cancellationToken);

        /// <summary>The scope an agent that declares <c>scoped: false</c> searches under.</summary>
        internal static readonly KnowledgeScope WholeCorpus =
            new() { Facets = new Dictionary<string, string>(StringComparer.Ordinal) };

        /// <summary>Binds the core to one agent's port, wiring and loggers.</summary>
        internal static Core Bind(KnowledgeBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);

            return (query, facets, turn, cancellationToken) => RunAsync(binding, query, facets, turn, cancellationToken);
        }

        private static async Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> RunAsync(
            KnowledgeBinding binding,
            string query,
            IReadOnlyDictionary<string, string>? facets,
            TurnInvocation? turn,
            CancellationToken cancellationToken)
        {
            (IKnowledgeRetrievalPort? port, ResolvedKnowledge? knowledge, string? agent, IKnowledgeCitationFormatter? citations, ILogger? logger) = binding;

            // No scope filters nothing, so it is the absent scope in disguise. The shared store
            // can only fail closed when EVERY agent is scoped, so in a mixed deployment this is
            // the only check standing between a scoped agent and every customer's cards. A null
            // turn — a run no conversation turn reached — searches the whole corpus unscoped and
            // names NoScope scoped: loud, never a leak.
            KnowledgeScope? composed = knowledge.Scoped ? turn?.Knowledge : WholeCorpus;

            if (knowledge.Scoped && composed is not { Facets.Count: > 0 })
            {
                return [KnowledgeNotices.Of(KnowledgeNotices.NoScope)];
            }

            KnowledgeScope scope = composed ?? WholeCorpus;

            (KnowledgeScope? narrowed, bool byTool) = ToolFacetOverlay.Apply(scope, facets);

            long started = Stopwatch.GetTimestamp();

            double? searched = null;
            KnowledgeScope under = narrowed;

            // One KnowledgeSearched per search, raised once its outcome is known: a search the store answered can
            // still fail on the agent's limit, the citations or the probe.
            try
            {
                (IReadOnlyList<KnowledgeCard>? cards, double latency) = await Under(narrowed).ConfigureAwait(false);
                searched = latency;

                if (cards.Count == 0 && byTool)
                {
                    under = scope;
                    (cards, latency) = await Under(scope).ConfigureAwait(false);
                    searched = latency;
                }

                IReadOnlyList<TextSearchProvider.TextSearchResult> results;
                if (cards.Count == 0
                    && knowledge.Mode == KnowledgeMode.Tool
                    && under.Facets.Count > 0)
                {
                    results = await KnowledgeProbe
                        .RunAsync(binding, under, query, turn?.Clarifications, turn?.CarriesHistory ?? false, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    List<KnowledgeCard> shown = Kept(cards, knowledge);
                    Cite(shown, knowledge, citations, turn?.Sources, turn?.OuterCallId);
                    results = Map(shown, knowledge, citations);
                }

                RaiseSearched(turn, query, under, cards.Count, latency, failure: null);
                return results;
            }
            catch (Exception failure) when (!KnowledgeCancellation.ByCaller(failure, cancellationToken))
            {
                double latency = searched ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                KnowledgeAuditRecord.LogView record = KnowledgeSearchRecord
                    .Of(agent, knowledge, query, under, [], latency, failure)
                    .ForLog();

                Log.KnowledgeRetrievalFailed(logger, agent, record, failure);

                RaiseSearched(turn, query, under, hits: 0, latency, $"{failure.GetType().Name}: {failure.Message}");

                return [KnowledgeNotices.Of(KnowledgeNotices.Unreachable)];
            }

            async Task<(IReadOnlyList<KnowledgeCard> Cards, double LatencyMs)> Under(KnowledgeScope open)
            {
                IReadOnlyList<KnowledgeCard> cards = await port.SearchAsync(query, open, cancellationToken).ConfigureAwait(false);

                double latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    KnowledgeAuditRecord.LogView record = KnowledgeSearchRecord
                        .Of(agent, knowledge, query, open, cards, latency, failure: null)
                        .ForLog();

                    Log.KnowledgeRetrieved(logger, agent, cards.Count, record);
                }

                return (cards, latency);
            }
        }

        private static void RaiseSearched(TurnInvocation? turn, string query, KnowledgeScope under, int hits, double latency, string? failure)
        {
            if (turn?.Hooks is { } hooks)
            {
                _ = hooks.Raise(new KnowledgeSearched(
                    hooks.Scope(turn.TurnIndex, turn.Stage), query, under, hits, TimeSpan.FromMilliseconds(latency), failure));
            }
        }

        /// <summary>Cuts one search down to the agent's <c>limit:</c>.</summary>
        /// <param name="cards">What the store returned, best first, links last.</param>
        /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
        /// <returns>The cards this agent is shown.</returns>
        private static List<KnowledgeCard> Kept(IReadOnlyList<KnowledgeCard> cards, ResolvedKnowledge knowledge)
        {
            List<KnowledgeCard> kept = [];
            int ranked = 0;

            foreach (KnowledgeCard card in cards)
            {
                if (!card.ViaLink && ranked++ >= knowledge.Limit)
                {
                    continue;
                }

                kept.Add(card);
            }

            return kept;
        }

        /// <summary>Cites what this search read, for the caller's screen.</summary>
        /// <param name="cards">The cards the agent is actually shown, after <see cref="Kept"/> cuts the search down to the agent's <c>limit:</c> — not everything the store returned.</param>
        /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
        /// <param name="citations">The wording <c>providers.knowledge.citation</c> named.</param>
        /// <param name="port">What the turn cites into, or <see langword="null"/> outside a turn.</param>
        /// <param name="callId">The outermost tool call this search runs inside, or <see langword="null"/> outside one.</param>
        private static void Cite(
            IReadOnlyList<KnowledgeCard> cards,
            ResolvedKnowledge knowledge,
            IKnowledgeCitationFormatter citations,
            TurnSources? port,
            string? callId)
        {
            if (!knowledge.Citations || port is null)
            {
                return;
            }

            foreach (KnowledgeCard card in cards)
            {
                if (KnowledgeSourceMapper.ToSource(card, citations) is { } source)
                {
                    port.Publish(source, callId);
                }
            }
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

            foreach (KnowledgeCard card in cards)
            {
                mapped.Add(KnowledgeCardMapper.ToResult(card, knowledge.Citations, citations));
            }

            return mapped;
        }
    }
}

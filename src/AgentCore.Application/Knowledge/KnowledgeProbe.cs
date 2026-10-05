using System.Diagnostics;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Runtime.Clarification;

namespace AgentCore.Application.Knowledge
{
    /// <summary>
    /// The probe. When a scoped search clears no card, it drops one wildcard-filled facet,
    /// searches again, and names what the wider search holds.
    /// </summary>
    internal static class KnowledgeProbe
    {
        /// <summary>Runs the probe.</summary>
        /// <param name="binding">The agent's port, wiring and logger. The probe's own second search reads the same port.</param>
        /// <param name="scope">The live scope the main search just ran under.</param>
        /// <param name="query">The search text the framework composed.</param>
        /// <param name="clarifications">The conversation's ambiguity holder, or <see langword="null"/> inside a nested tool call.</param>
        /// <param name="carriesHistory">Whether this row's session carries the caller's own history.</param>
        /// <param name="cancellationToken">The caller's own token — cancelling this is the caller hanging up, not a timeout.</param>
        /// <returns>What the probe found: its note, or the "holds nothing" notice.</returns>
        internal static async Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> RunAsync(
            KnowledgeBinding binding,
            KnowledgeScope scope,
            string query,
            Clarifications? clarifications,
            bool carriesHistory,
            CancellationToken cancellationToken)
        {
            // A nested tool call runs with the holder stripped from its invocation, so a delegated
            // run's own search cannot latch, count or record — but a scoped run still owes the caller
            // the notice.
            if (clarifications is null || ProbeWiring.From(binding.Knowledge.Clarification) is not { } wiring)
            {
                return [KnowledgeNotices.Of(KnowledgeNotices.Empty)];
            }

            // Recomputed fresh on every call in the turn — including one that arrives after the
            // turn's probe has already run — so it must stay cheap, deterministic and repeatable rather
            // than mutate anything. Nothing is latched by this exit: a second call that also finds no
            // droppable facet simply reaches this same conclusion again.
            if (DroppableFacet(wiring, scope, clarifications) is not { } facet)
            {
                return [KnowledgeNotices.Of(KnowledgeNotices.Empty)];
            }

            Clarifications.Probe probe = clarifications.ClaimProbe();
            if (!probe.Won)
            {
                return await ReplayAsync(probe, wiring.Ambiguity, cancellationToken).ConfigureAwait(false);
            }

            ClaimedProbe claimed = new(clarifications, probe, facet);

            // Every way out of the winner's path has to resolve the latch. Fail() is the catch-all: it
            // does nothing once an outcome has been published, and where nothing was published it wakes
            // the turn's other callers instead of leaving them to wait out the full margin for an answer
            // that a throw already took away.
            try
            {
                // The latch and the increment both belong before the search runs. An increment
                // placed after it would let a probe that always times out offer the same facet forever; a
                // latch placed after it would leave a throwing first call's latch unset, so a second call
                // in the same turn would re-run the search and advance probeAsks a second time.
                clarifications.Update(facet, s => s.ProbeAsks++);

                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(wiring.Ambiguity.ProbeDeadlineSeconds));

                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

                IReadOnlyList<KnowledgeCard> probeCards;

                try
                {
                    probeCards = await SearchAsync(binding, WithoutFacet(scope, facet), query, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception failure) when (KnowledgeCancellation.ByCaller(failure, timeout, cancellationToken))
                {
                    // The caller hung up: nothing was asked and nothing was learned, so the facet must not
                    // be charged for a turn that never happened. The payload is FAILED, not dropped, so
                    // every waiter wakes rather than burning its own wait margin on an answer that will
                    // never come — and the latch stays claimed, so the corpse of this cancelled turn
                    // cannot probe again.
                    clarifications.Update(facet, s => s.ProbeAsks--);
                    throw;
                }
                catch (Exception failure)
                {
                    // A throw or a timeout that is not caller cancellation: the main search already
                    // answered for reachability, so this says "holds nothing", never "unreachable".
                    Log.KnowledgeProbeFailed(binding.Logger, binding.Agent, facet, failure);

                    return Publish(probe, KnowledgeNotices.Empty);
                }

                return Name(binding, wiring, claimed, probeCards, carriesHistory);
            }
            finally
            {
                probe.Fail();
            }
        }

        /// <summary>The probe's own second search, under the narrowed scope, logged like the main one.</summary>
        private static async Task<IReadOnlyList<KnowledgeCard>> SearchAsync(
            KnowledgeBinding binding,
            KnowledgeScope narrowedScope,
            string query,
            CancellationToken deadline)
        {
            long started = Stopwatch.GetTimestamp();

            IReadOnlyList<KnowledgeCard> probeCards = await binding.Port.SearchAsync(query, narrowedScope, deadline).ConfigureAwait(false);

            if (binding.Logger.IsEnabled(LogLevel.Debug))
            {
                KnowledgeAuditRecord.LogView record = KnowledgeSearchRecord.Of(
                    binding.Agent,
                    binding.Knowledge,
                    query,
                    narrowedScope,
                    probeCards,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    failure: null).ForLog();

                Log.KnowledgeRetrieved(binding.Logger, binding.Agent, probeCards.Count, record);
            }

            return probeCards;
        }

        /// <summary>Replays the outcome of the one probe this turn already claimed.</summary>
        private static async Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> ReplayAsync(
            Clarifications.Probe probe,
            KnowledgeAmbiguityConfiguration ambiguity,
            CancellationToken cancellationToken)
        {
            TimeSpan wait = TimeSpan.FromSeconds(ambiguity.ProbeDeadlineSeconds + ambiguity.ProbeWaitMarginSeconds);

            try
            {
                return await probe.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (!KnowledgeCancellation.ByCaller(failure, cancellationToken))
            {
                // The winner's own search already answered for reachability (the catch around the probe's own
                // search says the same thing): a wait that timed out, or a payload the winner failed via Probe.Fail(),
                // is never grounds to tell this caller the store is unreachable.
                return [KnowledgeNotices.Of(KnowledgeNotices.Empty)];
            }
        }

        /// <summary>Reads the candidates out of the probe's cards and decides what to say.</summary>
        private static IReadOnlyList<TextSearchProvider.TextSearchResult> Name(
            KnowledgeBinding binding,
            ProbeWiring wiring,
            ClaimedProbe claimed,
            IReadOnlyList<KnowledgeCard> probeCards,
            bool carriesHistory)
        {
            (Clarifications? clarifications, Clarifications.Probe? probe, string? facet) = claimed;

            // The value at the facet's payload path is a string or a list of strings alike —
            // the real corpus stores arrays, and a cast straight to string would silently return null for
            // every card on a multi-model collection.
            string path = wiring.Template.Resolve(facet);
            SortedSet<string> union = new(StringComparer.Ordinal);

            union.UnionWith(probeCards
                .SelectMany(card => FacetValues(card, path))
                .Where(value => !string.Equals(value, wiring.WildcardValue, StringComparison.Ordinal)));

            Log.KnowledgeProbeRan(binding.Logger, binding.Agent, facet, union.Count);

            if (union.Count == 0)
            {
                return Publish(probe, KnowledgeNotices.Empty);
            }

            // probeAsks was already advanced before the second search; nothing below chooses anything but the
            // message.
            Clarifications.LastNamed wouldName = Clarifications.LastNamed.For(union, wiring.Ambiguity.MaxCandidates);
            List<string> candidates = [.. union];

            // Drawn for the record rather than for the message: on a graph row AgentCore cannot know
            // whether the participant's own tool result ever reached the caller, so the note still goes
            // out, but the record of what was named — which arms the tie-break — does not. The flag
            // arrives as a parameter because the probe runs inside a search delegate that is never
            // handed a session to compare.

            // Whether the note repeats what was last named, and the record that follows from it, are one
            // transition under one lock acquisition. Deciding from an earlier Read() and writing after
            // would let a concurrent participant on the same conversation slip between the two.
            bool repeats = false;

            clarifications.Update(facet, s =>
            {
                repeats = wouldName.Names(s.LastNamed);

                if (!repeats && carriesHistory)
                {
                    s.LastNamed = wouldName;
                }
            });

            if (repeats)
            {
                return Publish(probe, KnowledgeNotices.Empty);
            }

            string description = ClarificationText.DescriptionOf(facet, wiring.SlotDescriptions);

            return Publish(probe, ClarificationText.Note(description, candidates, wiring.Ambiguity.MaxCandidates));
        }

        /// <summary>Hands one notice to this caller and to every other search in the turn alike.</summary>
        private static IReadOnlyList<TextSearchProvider.TextSearchResult> Publish(
            Clarifications.Probe probe, string text)
        {
            IReadOnlyList<TextSearchProvider.TextSearchResult> outcome = [KnowledgeNotices.Of(text)];
            probe.Publish(outcome);
            return outcome;
        }

        /// <summary>
        /// The first facet, in <c>fromState</c> declaration order, the wildcard filled and that
        /// dropping would not empty the scope or skip a slot at its ask cap.
        /// </summary>
        private static string? DroppableFacet(ProbeWiring wiring, KnowledgeScope scope, Clarifications clarifications)
        {
            // The scope's only facet is undroppable — opening it empty is what a scoped store
            // refuses. This holds for every candidate alike, so no candidate can be droppable at all.
            if (scope.Facets.Count <= 1)
            {
                return null;
            }

            foreach (string name in wiring.FromState)
            {
                // The wildcard filled it — its value is the wildcard's own, and the facet is named
                // among the ones the wildcard is allowed to widen. Origins overrules the value where it
                // has an entry: a host that pinned a facet to the wildcard literal meant "every value of
                // it", and widening that facet would overrule an instruction rather than recover a lost
                // one. A scope composed without origins carries none, so an absent entry falls back to
                // the value alone.
                if (!wiring.WildcardFacets.Contains(name, StringComparer.Ordinal)
                    || !scope.Facets.TryGetValue(name, out string? value)
                    || !string.Equals(value, wiring.WildcardValue, StringComparison.Ordinal)
                    || (scope.Origins.TryGetValue(name, out KnowledgeFacetOrigin origin)
                        && origin != KnowledgeFacetOrigin.Wildcard))
                {
                    continue;
                }

                // The probe's own counter is monotone and capped at maxAsks.
                if (clarifications.Read(name).ProbeAsks >= wiring.Ambiguity.MaxAsks)
                {
                    continue;
                }

                return name;
            }

            return null;
        }

        /// <summary>Opens the same scope with one facet removed, for the probe's second search.</summary>
        /// <param name="scope">The scope the main search ran under.</param>
        /// <param name="facet">The facet to drop.</param>
        /// <returns>The narrowed scope.</returns>
        private static KnowledgeScope WithoutFacet(KnowledgeScope scope, string facet)
        {
            Dictionary<string, string> facets = new(scope.Facets, ComparerOf(scope.Facets));
            _ = facets.Remove(facet);

            Dictionary<string, KnowledgeFacetOrigin> origins = new(scope.Origins, ComparerOf(scope.Origins));
            _ = origins.Remove(facet);

            return scope with { Facets = facets, Origins = origins };
        }

        /// <summary>Reads back the comparer a scope's map was built on, or ordinal when it cannot be seen.</summary>
        private static IEqualityComparer<string> ComparerOf<T>(IReadOnlyDictionary<string, T> map)
        {
            return map is Dictionary<string, T> concrete ? concrete.Comparer : StringComparer.Ordinal;
        }

        /// <summary>
        /// Walks a dotted path into one card's <c>Extras</c>, reading a string or a list of
        /// strings alike — the shape <c>QdrantPointConverter</c> gives a Qdrant scalar or list value.
        /// </summary>
        private static IEnumerable<string> FacetValues(KnowledgeCard card, string path)
        {
            object? current = PayloadPath.Read(card.Extras, path);

            if (current is string single)
            {
                yield return single;
                yield break;
            }

            if (current is IReadOnlyList<object?> many)
            {
                foreach (object? item in many)
                {
                    if (item is string text)
                    {
                        yield return text;
                    }
                }
            }
        }
    }
}

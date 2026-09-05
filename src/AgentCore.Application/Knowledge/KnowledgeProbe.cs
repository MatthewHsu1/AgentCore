using System.Diagnostics;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// §8 steps 3-6: the probe. Channel 2 of the ambiguity design — when a scoped search clears no card,
/// it drops one wildcard-filled facet, searches again, and names what the wider search holds.
/// </summary>
internal static class KnowledgeProbe
{
    /// <summary>Runs the probe.</summary>
    /// <param name="port">The store the probe's own second search reads.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <param name="scope">The live scope the main search just ran under.</param>
    /// <param name="agent">The id of the agent that asked, for the log line.</param>
    /// <param name="query">The search text the framework composed.</param>
    /// <param name="logger">Where the probe's own log events go.</param>
    /// <param name="cancellationToken">The caller's own token — cancelling this is the caller hanging up, not a timeout.</param>
    /// <returns>What the probe found: its note, or the "holds nothing" notice.</returns>
    internal static async Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> RunAsync(
        IKnowledgeRetrievalPort port,
        ResolvedKnowledge knowledge,
        KnowledgeScope scope,
        string agent,
        string query,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // K42: a nested tool call strips the holder from the ambient, so a delegated run's own search
        // cannot latch, count or record — but a scoped run still owes the caller the notice.
        if (TurnAmbients.Current?.Clarifications is not { } clarifications)
        {
            return [KnowledgeNotices.Of(KnowledgeNotices.Empty)];
        }

        var wiring = knowledge.Clarification;

        // K19: with no ambiguity: configured (and so no wildcard, since the validator requires one
        // alongside the other) there is nothing for the probe to drop, and behaviour stays
        // byte-identical to the wildcard plan's own "holds nothing" notice.
        if (wiring.Ambiguity is not { } ambiguity
            || wiring.WildcardValue is not { } wildcardValue
            || wiring.WildcardFacets is not { Count: > 0 } wildcardFacets
            || wiring.Template is not { } template)
        {
            return [KnowledgeNotices.Of(KnowledgeNotices.Empty)];
        }

        // §8 step 3. Recomputed fresh on every call in the turn — including one that arrives after the
        // turn's probe has already run — so it must stay cheap, deterministic and repeatable rather
        // than mutate anything. Nothing is latched by this exit: a second call that also finds no
        // droppable facet simply reaches this same conclusion again.
        if (DroppableFacet(wiring.FromState, wildcardValue, wildcardFacets, scope, ambiguity, clarifications)
            is not { } facet)
        {
            return [KnowledgeNotices.Of(KnowledgeNotices.Empty)];
        }

        var probe = clarifications.ClaimProbe();
        if (!probe.Won)
        {
            return await ReplayAsync(probe, ambiguity, cancellationToken).ConfigureAwait(false);
        }

        // Every way out of the winner's path has to resolve the latch. Fail() is the catch-all: it
        // does nothing once an outcome has been published, and where nothing was published it wakes
        // the turn's other callers instead of leaving them to wait out the full margin for an answer
        // that a throw already took away.
        try
        {
            // §8 step 4: the latch and the increment both belong before the search runs. An increment
            // placed after it would let a probe that always times out offer the same facet forever; a
            // latch placed after it would leave a throwing first call's latch unset, so a second call
            // in the same turn would re-run the search and advance probeAsks a second time.
            clarifications.Update(facet, s => s.ProbeAsks++);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(ambiguity.ProbeDeadlineSeconds));
            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            IReadOnlyList<KnowledgeCard> probeCards;
            try
            {
                using var narrowed = KnowledgeScopeScope.Open(WithoutFacet(scope, facet));

                var started = Stopwatch.GetTimestamp();
                probeCards = await port.SearchAsync(query, deadline.Token).ConfigureAwait(false);

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    // Filed while the narrowed scope is still open, so the record names the scope this
                    // second store call actually ran under. Without it an operator sizing store load
                    // sees one record per turn for two calls.
                    var record = KnowledgeSearchRecord.Of(
                        agent,
                        knowledge,
                        query,
                        probeCards,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        failure: null).ForLog();

                    Log.KnowledgeRetrieved(logger, agent, probeCards.Count, record);
                }
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
                Log.KnowledgeProbeFailed(logger, agent, facet, failure);

                return Publish(probe, KnowledgeNotices.Empty);
            }

            return Name(probeCards, template, ambiguity, wiring, wildcardValue, facet, agent, logger, clarifications, probe);
        }
        finally
        {
            probe.Fail();
        }
    }

    /// <summary>K43: replays the outcome of the one probe this turn already claimed.</summary>
    private static async Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> ReplayAsync(
        Clarifications.Probe probe,
        KnowledgeAmbiguityConfiguration ambiguity,
        CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromSeconds(ambiguity.ProbeDeadlineSeconds + ambiguity.ProbeWaitMarginSeconds);

        try
        {
            return await probe.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!KnowledgeCancellation.ByCaller(failure, cancellationToken))
        {
            // The winner's own search already answered for reachability (§8 step 4's own catch says
            // the same thing): a wait that timed out, or a payload the winner failed via Probe.Fail(),
            // is never grounds to tell this caller the store is unreachable.
            return [KnowledgeNotices.Of(KnowledgeNotices.Empty)];
        }
    }

    /// <summary>§8 steps 5-6: reads the candidates out of the probe's cards and decides what to say.</summary>
    private static IReadOnlyList<TextSearchProvider.TextSearchResult> Name(
        IReadOnlyList<KnowledgeCard> probeCards,
        ScopeTemplate template,
        KnowledgeAmbiguityConfiguration ambiguity,
        ResolvedClarification wiring,
        string wildcardValue,
        string facet,
        string agent,
        ILogger logger,
        Clarifications clarifications,
        Clarifications.Probe probe)
    {
        // §8 step 5: the value at the facet's payload path is a string or a list of strings alike —
        // the real corpus stores arrays, and a cast straight to string would silently return null for
        // every card on a multi-model collection.
        var path = template.Resolve(facet);
        SortedSet<string> union = new(StringComparer.Ordinal);

        foreach (var card in probeCards)
        {
            foreach (var value in FacetValues(card, path))
            {
                if (!string.Equals(value, wildcardValue, StringComparison.Ordinal))
                {
                    union.Add(value);
                }
            }
        }

        Log.KnowledgeProbeRan(logger, agent, facet, union.Count);

        if (union.Count == 0)
        {
            return Publish(probe, KnowledgeNotices.Empty);
        }

        // §8 step 6. probeAsks was already advanced in step 4; nothing below chooses anything but the
        // message.
        var wouldName = Clarifications.LastNamed.For(union, ambiguity.MaxCandidates);
        var candidates = union.ToList();

        // K39, drawn for the record rather than for the message: on a graph row AgentCore cannot know
        // whether the participant's own tool result ever reached the caller, so the note still goes
        // out, but the record of what was named — which arms K21's tie-break — does not.
        var carriesHistory = TurnAmbients.Current?.Context?.CarriesHistory ?? false;

        // Whether the note repeats what was last named, and the pending list and record that follow
        // from it, are one transition under one lock acquisition. Deciding from an earlier Read() and
        // writing after would let a concurrent participant on the same call slip between the two.
        var repeats = false;

        clarifications.Update(facet, s =>
        {
            repeats = wouldName.Names(s.EffectiveLastNamed);

            if (repeats)
            {
                return;
            }

            // K41: the probe sets a pending list only where there is none. The linker's own list is
            // what the caller was actually answering, and a search's guess must never replace it.
            s.Pending ??= candidates;

            if (carriesHistory)
            {
                s.LastNamed = wouldName;
            }
        });

        if (repeats)
        {
            return Publish(probe, KnowledgeNotices.Empty);
        }

        var description = ClarificationText.DescriptionOf(facet, wiring.SlotDescriptions);

        return Publish(probe, ClarificationText.Note(description, candidates, ambiguity.MaxCandidates));
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
    /// §8 step 3: the first facet, in <c>fromState</c> declaration order, the wildcard filled and that
    /// dropping would not empty the scope, skip a slot at its ask cap, or repeat channel 1's own
    /// question this turn.
    /// </summary>
    private static string? DroppableFacet(
        IReadOnlyList<string> fromState,
        string wildcardValue,
        IReadOnlyList<string> wildcardFacets,
        KnowledgeScope scope,
        KnowledgeAmbiguityConfiguration ambiguity,
        Clarifications clarifications)
    {
        // K33: the scope's only facet is undroppable — opening it empty is what a scoped store
        // refuses. This holds for every candidate alike, so no candidate can be droppable at all.
        if (scope.Facets.Count <= 1)
        {
            return null;
        }

        foreach (var name in fromState)
        {
            // K14: the wildcard filled it — its value is the wildcard's own, and the facet is named
            // among the ones the wildcard is allowed to widen. Origins overrules the value where it
            // has an entry: a host that pinned a facet to the wildcard literal meant "every value of
            // it", and widening that facet would overrule an instruction rather than recover a lost
            // one. A scope composed without origins carries none, so an absent entry falls back to
            // the value alone.
            if (!wildcardFacets.Contains(name, StringComparer.Ordinal)
                || !scope.Facets.TryGetValue(name, out var value)
                || !string.Equals(value, wildcardValue, StringComparison.Ordinal)
                || (scope.Origins.TryGetValue(name, out var origin)
                    && origin != KnowledgeFacetOrigin.Wildcard))
            {
                continue;
            }

            var snapshot = clarifications.Read(name);

            // K22: the probe's own counter is monotone and capped at maxAsks. K41: channel 1 already
            // asked about this slot this turn, so the probe moves on rather than paying for a search
            // it would then abandon.
            if (snapshot.ProbeAsks >= ambiguity.MaxAsks || snapshot.AskedThisTurn)
            {
                continue;
            }

            return name;
        }

        return null;
    }

    /// <summary>Opens the same scope with one facet removed, for §8 step 4's second search.</summary>
    /// <param name="scope">The scope the main search ran under.</param>
    /// <param name="facet">The facet to drop.</param>
    /// <returns>The narrowed scope.</returns>
    /// <remarks>
    /// Each map keeps its own comparer. Rebuilding on a fixed ordinal comparer would narrow how a
    /// case-insensitive caller's facets match, so the second search would run under different matching
    /// rules than the first. Origins loses the facet alongside Facets, because it describes what the
    /// query actually filters on — a retrieval record built from a scope that kept it would name a
    /// facet the probe's search never constrained.
    /// </remarks>
    private static KnowledgeScope WithoutFacet(KnowledgeScope scope, string facet)
    {
        Dictionary<string, string> facets = new(scope.Facets, ComparerOf(scope.Facets));
        facets.Remove(facet);

        Dictionary<string, KnowledgeFacetOrigin> origins = new(scope.Origins, ComparerOf(scope.Origins));
        origins.Remove(facet);

        return scope with { Facets = facets, Origins = origins };
    }

    /// <summary>Reads back the comparer a scope's map was built on, or ordinal when it cannot be seen.</summary>
    private static IEqualityComparer<string> ComparerOf<T>(IReadOnlyDictionary<string, T> map)
        => map is Dictionary<string, T> concrete ? concrete.Comparer : StringComparer.Ordinal;

    /// <summary>
    /// §8 step 5: walks a dotted path into one card's <c>Extras</c>, reading a string or a list of
    /// strings alike — the shape <c>QdrantPointConverter</c> gives a Qdrant scalar or list value.
    /// </summary>
    private static IEnumerable<string> FacetValues(KnowledgeCard card, string path)
    {
        var current = PayloadPath.Read(card.Extras, path);

        if (current is string single)
        {
            yield return single;
            yield break;
        }

        if (current is IReadOnlyList<object?> many)
        {
            foreach (var item in many)
            {
                if (item is string text)
                {
                    yield return text;
                }
            }
        }
    }
}

using AgentCore.Application.Configuration.Parsing;
using System.Diagnostics;
using System.Reflection;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// §8: the probe. Channel 2 of the ambiguity design — inside the knowledge search itself, dropping
/// one wildcard-filled facet, re-searching, and naming the values it finds.
/// </summary>
/// <remarks>
/// Every test drives <c>KnowledgeProviderFactory</c>'s own search delegate directly, the same way
/// <c>KnowledgeProviderFactoryTests</c> does, rather than through <c>CallSession</c>: the probe reads
/// only the ambient (<see cref="Clarifications"/>, <see cref="KnowledgeScopeScope"/>,
/// <see cref="TurnContext"/>) and the resolved <c>knowledge:</c> block, so opening exactly those by
/// hand proves the same mechanism a real call would exercise, at a fraction of the setup. The
/// genuinely two-turn and delegation-shaped cases live in <c>CallSessionProbeTests</c>.
/// </remarks>
public sealed class KnowledgeProbeTests
{
    private const string Description = "The model, as printed on the machine.";

    // §8 steps 1-2: when the probe does not run at all.

    [Fact]
    public async Task Step1_MainSearchReturnedCards_TheProbeNeverRuns()
    {
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([Card("a")]));
        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Contains(results, r => r.Text == "card a");
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task Step2_UnscopedAgentsEmptySearch_ReturnsAnEmptyList()
    {
        // Acceptance: "an unscoped agent's empty search returns an empty list." The probe and the
        // "holds nothing" notice are for a scoped agent only (K19).
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));
        using var clarifications = OpenClarifications();

        var results = await InvokeSearchAsync(
            Provider(port, Resolved(["applies_to"], scoped: false)), "e33");

        Assert.Empty(results);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task NoAmbiguityConfigured_StaysByteIdenticalToTheWildcardPlan()
    {
        // K19: with no ambiguity: (and so no wildcard, no template) configured, behaviour is
        // byte-identical to the wildcard plan's own "holds nothing" notice — no second search.
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));
        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var plain = new ResolvedKnowledge(KnowledgeMode.Tool, 5, Citations: false, Scoped: true);
        var results = await InvokeSearchAsync(Provider(port, plain), "e33");

        Assert.Contains(results, r => r.Text.Contains("holds nothing", StringComparison.Ordinal));
        Assert.Equal(1, port.Calls);
    }

    // §8 step 3: which facets are droppable.

    [Fact]
    public async Task Step3_SingleFacetScope_IsUndroppable()
    {
        // K33: dropping the scope's only facet would open it empty, which a scoped store refuses.
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));
        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to"));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Contains(results, r => r.Text.Contains("holds nothing", StringComparison.Ordinal));
        // Only the main search ran. The probe never opened a second, narrowed scope.
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task Step3_MaxAsksZero_DropsNoFacet_AndEmitsW11sNotice()
    {
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));
        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("brand", "applies_to"));

        var knowledge = Resolved(["brand", "applies_to"], ambiguity: new KnowledgeAmbiguityConfiguration { MaxAsks = 0 });
        var results = await InvokeSearchAsync(Provider(port, knowledge), "e33");

        Assert.Single(results);
        Assert.Contains("holds nothing", results[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task Step3_FacetAtItsAskCap_IsSkipped_AndTheNextDroppableFacetIsTried()
    {
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        var clarificationsObject = new Clarifications();
        clarificationsObject.Update("brand", s => s.ProbeAsks = 2);
        using var clarifications = OpenClarifications(clarificationsObject);
        using var scope = KnowledgeScopeScope.Open(FullScope("brand", "applies_to"));

        var knowledge = Resolved(["brand", "applies_to"]);
        var results = await InvokeSearchAsync(Provider(port, knowledge), "e33");

        Assert.Contains(results, r => r.Text.Contains("ct900", StringComparison.Ordinal));
        // brand was skipped, not touched: it stays exactly where the test seeded it.
        Assert.Equal(2, clarificationsObject.Read("brand").ProbeAsks);
        Assert.Equal(1, clarificationsObject.Read("applies_to").ProbeAsks);
    }

    [Fact]
    public async Task Step3_FacetChannel1AlreadyAskedThisTurn_IsSkipped()
    {
        // K41: one slot is asked by at most one channel per turn. brand is skipped, so applies_to --
        // the only other droppable facet -- is what the probe's second search actually drops.
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        var clarificationsObject = new Clarifications();
        clarificationsObject.Update("brand", s => s.AskedThisTurn = true);
        using var clarifications = OpenClarifications(clarificationsObject);
        using var scope = KnowledgeScopeScope.Open(FullScope("brand", "applies_to"));

        var knowledge = Resolved(["brand", "applies_to"]);
        var results = await InvokeSearchAsync(Provider(port, knowledge), "e33");

        Assert.Contains(results, r => r.Text.Contains("ct900", StringComparison.Ordinal));
        Assert.Equal(0, clarificationsObject.Read("brand").ProbeAsks);
        Assert.Equal(1, clarificationsObject.Read("applies_to").ProbeAsks);
    }

    [Fact]
    public async Task Step4_DropsTheFirstDroppableFacet_InFromStateDeclarationOrder()
    {
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "brand"));

        // fromState declares applies_to before brand, so applies_to is tried first even though both
        // are equally droppable.
        var knowledge = Resolved(["applies_to", "brand"]);
        var results = await InvokeSearchAsync(Provider(port, knowledge), "e33");

        Assert.Contains(results, r => r.Text.Contains("ct900", StringComparison.Ordinal));
    }

    // §8 steps 5-6: reading candidates and choosing the message.

    [Fact]
    public async Task Steps5And6_FindsCandidates_SetsThePendingListAndEmitsTheNote()
    {
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to")
                ? []
                : [CardWithFacet("a", "applies_to", "ct900"), CardWithFacet("b", "applies_to", "ct900ent")]));

        var clarificationsObject = new Clarifications();
        using var clarifications = OpenClarifications(clarificationsObject);
        using var carries = TurnAmbientsTestScope.WithCarriesHistory(true);
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var knowledge = Resolved(["applies_to"], slotDescriptions: Descriptions(("applies_to", Description)));
        var results = await InvokeSearchAsync(Provider(port, knowledge), "e33");

        Assert.Equal(
            ClarificationText.Note(Description, ["ct900", "ct900ent"], KnowledgeAmbiguityConfiguration.DefaultMaxCandidates),
            Assert.Single(results).Text);

        var snapshot = clarificationsObject.Read("applies_to");
        Assert.Equal(["ct900", "ct900ent"], snapshot.Pending);
        Assert.Equal(Clarifications.LastNamedKind.Set, snapshot.LastNamed.Kind);
        Assert.Equal(
            new HashSet<string>(["ct900", "ct900ent"], StringComparer.Ordinal), snapshot.LastNamed.Values);
    }

    [Fact]
    public async Task Step5_ReadsAListShapedFacetValue_NotJustAString()
    {
        // K24's own example: a card tagged applies_to: ["ct900", "ct900ent"]. A cast straight to
        // string would return null for every card on a multi-model collection.
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to")
                ? []
                : [CardWithFacetList("a", "applies_to", ["ct900", "ct900ent"])]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        var text = Assert.Single(results).Text;
        Assert.Contains("ct900", text, StringComparison.Ordinal);
        Assert.Contains("ct900ent", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step5_WalksANestedTemplatePath()
    {
        const string template = "facets.{key}";
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to")
                ? []
                : [CardWithFacet("a", "applies_to", "ct900", template)]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var results = await InvokeSearchAsync(
            Provider(port, Resolved(["applies_to"], template: template)), "e33");

        Assert.Contains("ct900", Assert.Single(results).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K6_TheWildcardValueItselfIsDiscardedFromTheCandidates()
    {
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to")
                ? []
                : [CardWithFacet("a", "applies_to", "*"), CardWithFacet("b", "applies_to", "ct900")]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        var text = Assert.Single(results).Text;
        Assert.Contains("ct900", text, StringComparison.Ordinal);
        Assert.DoesNotContain("could be: *", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K24_OneCardIsNeverASpread()
    {
        // Acceptance: "one card is never a spread." One value produces a confirm question, not
        // "fewer than two -> holds nothing".
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var results = await InvokeSearchAsync(
            Provider(port, Resolved(["applies_to"], slotDescriptions: Descriptions(("applies_to", Description)))),
            "e33");

        Assert.Equal(
            ClarificationText.Note(Description, ["ct900"], KnowledgeAmbiguityConfiguration.DefaultMaxCandidates),
            Assert.Single(results).Text);
    }

    [Fact]
    public async Task MoreThanMaxCandidates_NamesNone()
    {
        // Acceptance: "more than maxCandidates names none." The probe still speaks — this is not the
        // "holds nothing" notice — but the rendered text omits every value.
        var many = Enumerable.Range(0, 7).Select(i => $"model{i}").ToArray();
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to")
                ? []
                : [.. many.Select((value, i) => CardWithFacet($"c{i}", "applies_to", value))]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var knowledge = Resolved(
            ["applies_to"], ambiguity: new KnowledgeAmbiguityConfiguration { MaxCandidates = 6 });
        var results = await InvokeSearchAsync(Provider(port, knowledge), "e33");

        var text = Assert.Single(results).Text;
        Assert.DoesNotContain("holds nothing", text, StringComparison.Ordinal);
        foreach (var value in many)
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task K34_ARepeatedIdenticalCandidateSet_EmitsHoldsNothing()
    {
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to")
                ? []
                : [CardWithFacet("a", "applies_to", "ct900"), CardWithFacet("b", "applies_to", "ct900ent")]));

        var clarificationsObject = new Clarifications();
        clarificationsObject.Update(
            "applies_to",
            s => s.LastNamed = Clarifications.LastNamed.Of(new HashSet<string>(["ct900", "ct900ent"], StringComparer.Ordinal)));
        using var clarifications = OpenClarifications(clarificationsObject);
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Contains("holds nothing", Assert.Single(results).Text, StringComparison.Ordinal);

        // probeAsks still advances -- the slot was tried, it just had nothing new to say.
        Assert.Equal(1, clarificationsObject.Read("applies_to").ProbeAsks);
    }

    [Fact]
    public async Task ZeroCandidatesFound_EmitsHoldsNothing()
    {
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Contains("holds nothing", Assert.Single(results).Text, StringComparison.Ordinal);
    }

    // K39: a graph document's probe still speaks, but must not record what it named.

    [Fact]
    public async Task K39_AGraphDocumentsProbe_EmitsItsNote_AndRecordsNoLastNamed()
    {
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        var clarificationsObject = new Clarifications();
        using var clarifications = OpenClarifications(clarificationsObject);
        using var carries = TurnAmbientsTestScope.WithCarriesHistory(false);
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Contains("ct900", Assert.Single(results).Text, StringComparison.Ordinal);

        var snapshot = clarificationsObject.Read("applies_to");
        // The pending list is still set: a later turn on the SAME graph row's session may still get
        // an answer through the linker. Only the RECORD of what was named is withheld (K21's
        // tie-break rests on the caller having heard the list, which AgentCore cannot know here).
        Assert.Equal(["ct900"], snapshot.Pending);
        Assert.Equal(Clarifications.LastNamedKind.None, snapshot.LastNamed.Kind);
    }

    // K42: no holder, no probe -- but the notice is still owed to a scoped run.

    [Fact]
    public async Task K42_NoHolderOnTheAmbient_TheProbeDoesNotSearch_ButStillEmitsTheNotice()
    {
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        // No Clarifications opened on the ambient at all -- the K42 strip inside a nested tool call.
        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Contains("holds nothing", Assert.Single(results).Text, StringComparison.Ordinal);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task K42_ASubAgentThatSearchesFirst_WritesNoLastNamed_AndTheCallersOwnSearchStillCounts()
    {
        // Acceptance: "a sub-agent that searches first writes no lastNamed, and the caller's own
        // search still counts." Simulates exactly what AuditingFunctionInvokingChatClient.
        // InvokeFunctionAsync does for a NESTED tool call: it strips Clarifications from the ambient,
        // leaving the outer call id in place.
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        var clarificationsObject = new Clarifications();
        var knowledge = Resolved(["applies_to"]);
        var provider = Provider(port, knowledge);

        using (OpenClarifications(clarificationsObject))
        using (KnowledgeScopeScope.Open(FullScope("applies_to", "other")))
        {
            IReadOnlyList<TextSearchProvider.TextSearchResult> nested;
            using (TurnAmbients.Amend(a => a with { Clarifications = null }))
            {
                nested = await InvokeSearchAsync(provider, "the sub-agent's own question");
            }

            Assert.Contains("holds nothing", Assert.Single(nested).Text, StringComparison.Ordinal);
            Assert.Equal(0, clarificationsObject.Read("applies_to").ProbeAsks);
            // Only the sub-agent's own main search ran; its own probe search never reached the port.
            Assert.Equal(1, port.Calls);

            var callers = await InvokeSearchAsync(provider, "the caller's own question");

            Assert.Contains("ct900", Assert.Single(callers).Text, StringComparison.Ordinal);
            Assert.Equal(1, clarificationsObject.Read("applies_to").ProbeAsks);
        }
    }

    // K25: every notice this design emits carries the reserved source name, and none is citable.

    [Fact]
    public async Task EveryOutcome_CarriesTheReservedSourceName()
    {
        var holdsNothing = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));
        using (OpenClarifications())
        using (KnowledgeScopeScope.Open(FullScope("applies_to", "other")))
        {
            var results = await InvokeSearchAsync(Provider(holdsNothing, Resolved(["applies_to"])), "e33");
            Assert.All(results, r => Assert.Equal(KnowledgeNotices.SourceName, r.SourceName));
        }

        var named = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));
        using (OpenClarifications())
        using (KnowledgeScopeScope.Open(FullScope("applies_to", "other")))
        {
            var results = await InvokeSearchAsync(Provider(named, Resolved(["applies_to"])), "e33");
            Assert.All(results, r => Assert.Equal(KnowledgeNotices.SourceName, r.SourceName));
        }
    }

    [Fact]
    public async Task K23_AProbeSpread_PublishesNoSources()
    {
        // Acceptance: "a probe spread publishes no sources." The probe runs between Kept and Cite, so
        // nothing it discards -- and nothing it names -- is ever published as a citable source.
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to")
                ? []
                : [CardWithFacet("a", "applies_to", "ct900"), CardWithFacet("b", "applies_to", "ct900ent")]));

        var provider = Provider(port, Resolved(["applies_to"], citations: true));
        TurnSources sources = new();

        using (OpenClarifications())
        using (KnowledgeScopeScope.Open(FullScope("applies_to", "other")))
        using (TurnAmbientsTestScope.WithSources(sources))
        using (TurnAmbientsTestScope.WithOuterCall("call-1"))
        {
            await InvokeSearchAsync(provider, "e33");
        }

        Assert.Empty(sources.TakeFor("call-1"));
    }

    // K32: the probe's own try, its own budget, its own log events.

    [Fact]
    public async Task Probe_SecondSearchThrows_EmitsHoldsNothing_AndLogsItsOwnFailureEvent()
    {
        InvalidOperationException boom = new("qdrant is down, second leg");
        var port = new ProbeFakePort((facets, _) =>
            facets.ContainsKey("applies_to")
                ? Task.FromResult<IReadOnlyList<KnowledgeCard>>([])
                : throw boom);

        RecordingLoggerFactory loggers = new();
        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var provider = KnowledgeProviderFactory.Create(
            port, Resolved(["applies_to"]), "agent-under-test", new SourceLocatorCitationFormatter(), loggers);
        var results = await InvokeSearchAsync(provider, "e33");

        Assert.Contains("holds nothing", Assert.Single(results).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable", Assert.Single(results).Text, StringComparison.OrdinalIgnoreCase);

        var line = Assert.Single(loggers.Of(17));
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("agent-under-test", line.Field<string>("Agent"));
        Assert.Equal("applies_to", line.Field<string>("Facet"));
        Assert.Same(boom, line.Exception);

        // The main search's own success is unaffected: nothing about it was ever a failure.
        Assert.Empty(loggers.Of(12));
    }

    [Fact]
    public async Task Probe_SecondSearchTimesOut_IsTreatedAsAFailure_NeverAsUnreachable()
    {
        var port = new ProbeFakePort(async (facets, cancellationToken) =>
        {
            if (facets.ContainsKey("applies_to"))
            {
                return [];
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        });

        var clarificationsObject = new Clarifications();
        using var clarifications = OpenClarifications(clarificationsObject);
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var knowledge = Resolved(
            ["applies_to"],
            ambiguity: new KnowledgeAmbiguityConfiguration { ProbeDeadlineSeconds = 1, ProbeWaitMarginSeconds = 1 });
        var results = await InvokeSearchAsync(Provider(port, knowledge), "e33");

        Assert.Contains("holds nothing", Assert.Single(results).Text, StringComparison.Ordinal);

        // §8 step 4's increment ran before the search; a timeout that is not caller cancellation
        // never rolls it back the way K43's own cancellation row does.
        Assert.Equal(1, clarificationsObject.Read("applies_to").ProbeAsks);
    }

    // K43: at most once per turn -- the latch, the replay, and cancellation.

    [Fact]
    public async Task K43_ThreeSearchCallsInOneTurn_RunOneProbe_AndIncrementOnce()
    {
        var port = new ProbeFakePort((facets, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        var clarificationsObject = new Clarifications();
        var provider = Provider(port, Resolved(["applies_to"]));

        using (OpenClarifications(clarificationsObject))
        using (KnowledgeScopeScope.Open(FullScope("applies_to", "other")))
        {
            var first = await InvokeSearchAsync(provider, "e33");
            var second = await InvokeSearchAsync(provider, "e33 again");
            var third = await InvokeSearchAsync(provider, "e33 once more");

            Assert.Same(first[0], second[0]);
            Assert.Same(first[0], third[0]);
        }

        // Every call's own main search runs (three), and only the winner's search reached the
        // narrowed scope (one): four port calls, one probe.
        Assert.Equal(4, port.Calls);
        Assert.Equal(1, clarificationsObject.Read("applies_to").ProbeAsks);
    }

    [Fact]
    public async Task K43_TwoCalls_TheFirstEmittedHoldsNothing_TheSecondReplaysTheSameBytes()
    {
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));
        var provider = Provider(port, Resolved(["applies_to"]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var first = await InvokeSearchAsync(provider, "e33");
        var second = await InvokeSearchAsync(provider, "e33 again");

        // InvokeSearchAsync itself materialises a fresh list with ToList(), so the replay proof is on
        // the element the two lists share, not on the wrapper InvokeSearchAsync just allocated.
        Assert.Same(first[0], second[0]);
    }

    [Fact]
    public async Task K43_TwoCalls_TheFirstProbeThrows_TheSecondReplaysHoldsNothing()
    {
        var thrown = 0;
        var port = new ProbeFakePort((facets, _) =>
        {
            if (facets.ContainsKey("applies_to"))
            {
                return Task.FromResult<IReadOnlyList<KnowledgeCard>>([]);
            }

            thrown++;
            throw new InvalidOperationException("down");
        });

        var provider = Provider(port, Resolved(["applies_to"]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var first = await InvokeSearchAsync(provider, "e33");
        var second = await InvokeSearchAsync(provider, "e33 again");

        Assert.Equal(1, thrown);
        Assert.Same(first[0], second[0]);
        Assert.Contains("holds nothing", first[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K43_ACancelledProbe_RollsBackTheIncrement_FailsThePayload_AndKeepsTheLatch()
    {
        using CancellationTokenSource caller = new();
        TaskCompletionSource probeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var port = new ProbeFakePort(async (facets, cancellationToken) =>
        {
            if (facets.ContainsKey("applies_to"))
            {
                return [];
            }

            probeStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        });

        var clarificationsObject = new Clarifications();
        var provider = Provider(port, Resolved(["applies_to"]));

        using var clarifications = OpenClarifications(clarificationsObject);
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var winner = InvokeSearchWithCallerTokenAsync(provider, "e33", caller.Token);

        await probeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => winner);

        Assert.Equal(0, clarificationsObject.Read("applies_to").ProbeAsks);

        // The latch stays claimed: a fresh caller this turn does not get to run a second probe search.
        Assert.False(clarificationsObject.ClaimProbe().Won);
    }

    [Fact]
    public async Task K43_ACancelledProbe_WakesAGenuinelyWaitingLoser_RatherThanLettingItRunOutItsOwnClock()
    {
        // The row above proves the winner's own rollback and the latch staying claimed, but neither
        // of those observes Probe.Fail() actually doing anything: nothing there ever constructs
        // a second, genuinely waiting caller. This test does: a real second search call reaches
        // Probe.WaitAsync while the winner is still blocked in its own search, and only the
        // elapsed time tells correct from broken.
        //
        // The loser's own token is never cancelled -- deliberately unrelated to the winner's
        // caller.Token, unlike a same-turn call that would share it. A shared token was tried first
        // and measured to make this row untestable: Task.WaitAsync(timeout, token) reacts to a
        // cancelled TOKEN exactly as fast as it reacts to the awaited TASK being cancelled, so with
        // one shared token the loser wakes just as quickly whether or not Probe.Fail() ever
        // runs -- removing the call and re-running this same test with a shared token still passed,
        // in under 150 ms. An unrelated token closes that hole: the loser's own wait then has exactly
        // one way to end early -- the shared payload being failed -- and otherwise runs out its own
        // two-second clock (ProbeDeadlineSeconds + ProbeWaitMarginSeconds), which is what the timing
        // assertion below tells apart. (Both outcomes return the same "holds nothing" notice: the
        // loser's own catch around Probe.WaitAsync tells its caller's own cancellation apart
        // from a failed payload or an expired wait, and only the latter two reach here -- so only the
        // speed differs, which is why the assertion below is on speed and not on the returned text.)
        using CancellationTokenSource caller = new();
        TaskCompletionSource probeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var port = new ProbeFakePort(async (facets, cancellationToken) =>
        {
            if (facets.ContainsKey("applies_to"))
            {
                return [];
            }

            probeStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        });

        // Short enough that a genuine regression fails this test in about two seconds rather than
        // hanging the suite; long enough that "woke immediately" and "ran out the clock" are
        // unmistakably different durations.
        var knowledge = Resolved(
            ["applies_to"],
            ambiguity: new KnowledgeAmbiguityConfiguration { ProbeDeadlineSeconds = 1, ProbeWaitMarginSeconds = 1 });
        var provider = Provider(port, knowledge);

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var winner = InvokeSearchWithCallerTokenAsync(provider, "e33", caller.Token);

        // Only reached after the claim, the increment, and the narrowed scope's own search have
        // all already run, so the loser below is guaranteed to lose the race, not win it.
        await probeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var loser = InvokeSearchAsync(provider, "e33 from a second caller");

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => winner);

        var loserResult = await loser;
        stopwatch.Stop();

        Assert.Contains("holds nothing", Assert.Single(loserResult).Text, StringComparison.Ordinal);

        // The wait's own ceiling is two seconds. A loser that woke well under that woke because
        // Probe.Fail() failed the shared payload; one that took close to two seconds ran out
        // its own clock instead, which is exactly the defect this row exists to catch.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"the loser took {stopwatch.Elapsed} to return; that is close enough to its own two-second "
                + "wait ceiling that it may have run out its own clock rather than observed the failed payload.");
    }

    [Fact]
    public async Task K43_ProbeThatThrowsEveryTurn_StillAdvancesProbeAsks()
    {
        // Acceptance: "a probe that throws every turn still advances probeAsks." Two turns,
        // simulated with the same BeginTurn a real CallSession calls: the latch and the per-turn mark
        // reset, the counter does not.
        var attempts = 0;
        var port = new ProbeFakePort((facets, _) =>
        {
            if (facets.ContainsKey("applies_to"))
            {
                return Task.FromResult<IReadOnlyList<KnowledgeCard>>([]);
            }

            attempts++;
            throw new InvalidOperationException("down");
        });

        var clarificationsObject = new Clarifications();
        var provider = Provider(port, Resolved(["applies_to"]));

        using (OpenClarifications(clarificationsObject))
        using (KnowledgeScopeScope.Open(FullScope("applies_to", "other")))
        {
            await InvokeSearchAsync(provider, "turn one");
        }

        Assert.Equal(1, clarificationsObject.Read("applies_to").ProbeAsks);

        clarificationsObject.BeginTurn();

        using (OpenClarifications(clarificationsObject))
        using (KnowledgeScopeScope.Open(FullScope("applies_to", "other")))
        {
            await InvokeSearchAsync(provider, "turn two");
        }

        Assert.Equal(2, clarificationsObject.Read("applies_to").ProbeAsks);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Loser_WaitExpiresBeforeAnyOutcomeIsPublished_EmitsHoldsNothing_NeverUnreachable()
    {
        // C1's own gap: the probe is claimed directly, exactly as a real winner's ProbeAsync would,
        // but its payload is never published -- neither Publish nor Fail. With ProbeDeadlineSeconds
        // and ProbeWaitMarginSeconds both 0, the loser's own wait times out almost at once, which is
        // the row the fix exists for:
        // a TimeoutException reaching SearchAsync's outer catch would blame the main search, which
        // never ran a second time and never failed.
        var port = new ProbeFakePort((_, _) => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]));

        var clarificationsObject = new Clarifications();
        Assert.True(clarificationsObject.ClaimProbe().Won);

        using var clarifications = OpenClarifications(clarificationsObject);
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var knowledge = Resolved(
            ["applies_to"],
            ambiguity: new KnowledgeAmbiguityConfiguration { ProbeDeadlineSeconds = 0, ProbeWaitMarginSeconds = 0 });
        var results = await InvokeSearchAsync(Provider(port, knowledge), "e33");

        var text = Assert.Single(results).Text;
        Assert.Contains("holds nothing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvedKnowledge_TheAmbiguityMembers_RoundTripThroughInit()
    {
        var ambiguity = new KnowledgeAmbiguityConfiguration { MaxAsks = 3 };
        var descriptions = Descriptions(("applies_to", Description));

        var knowledge = new ResolvedKnowledge(KnowledgeMode.Tool, 5, Citations: false, Scoped: true)
        {
            Clarification = new ResolvedClarification
            {
                WildcardValue = "*",
                WildcardFacets = ["applies_to"],
                FromState = ["applies_to"],
                SlotDescriptions = descriptions,
                Ambiguity = ambiguity,
                Template = ScopeTemplate.Parse("facets.{key}"),
            },
        };

        var wiring = knowledge.Clarification;
        Assert.Equal("*", wiring.WildcardValue);
        Assert.Equal(["applies_to"], wiring.WildcardFacets);
        Assert.Equal(["applies_to"], wiring.FromState);
        Assert.Same(descriptions, wiring.SlotDescriptions);
        Assert.Same(ambiguity, wiring.Ambiguity);
        Assert.Equal("facets.{key}", wiring.Template?.Raw);
    }

    [Fact]
    public async Task Step3_HostPinnedTheWildcardValue_IsNotDropped()
    {
        // A host that opens a facet on the wildcard literal is saying "every value of it", not "I did
        // not know". Origins is where that difference is recorded, so the value alone cannot decide
        // droppability: widening here would overrule the host rather than recover a lost value.
        var port = new ProbeFakePort(facets => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(ScopeWithOrigins(
            ("brand", "acme", KnowledgeFacetOrigin.Host),
            ("applies_to", "*", KnowledgeFacetOrigin.Host)));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Single(results);
        Assert.Contains("holds nothing", results[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task Step3_OriginsRecordTheWildcardFilledIt_SoTheFacetIsDropped()
    {
        // The row above's control: the same scope and the same value, differing only in what Origins
        // records, does reach the probe's second search.
        var port = new ProbeFakePort(facets => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(ScopeWithOrigins(
            ("brand", "acme", KnowledgeFacetOrigin.Host),
            ("applies_to", "*", KnowledgeFacetOrigin.Wildcard)));

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Single(results);
        Assert.Contains("ct900", results[0].Text, StringComparison.Ordinal);
        Assert.Equal(2, port.Calls);
    }

    [Fact]
    public async Task Step4_TheNarrowedScopeKeepsTheCallersOwnComparer()
    {
        // The probe rebuilds the scope to drop one facet. Rebuilding on a fixed ordinal comparer would
        // silently narrow how a case-insensitive caller's remaining facets match, so the second search
        // would run under different matching rules than the first.
        var port = new ProbeFakePort(facets => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? []
            : facets.ContainsKey("brand") ? [CardWithFacet("a", "applies_to", "ct900")]
            : []));

        Dictionary<string, string> facets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Brand"] = "acme",
            ["applies_to"] = "*",
        };

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(new KnowledgeScope { Facets = facets });

        var results = await InvokeSearchAsync(Provider(port, Resolved(["applies_to"])), "e33");

        Assert.Single(results);
        Assert.Contains("ct900", results[0].Text, StringComparison.Ordinal);
        Assert.Equal(2, port.Calls);
    }

    [Fact]
    public async Task Step4_TheDeadlineFiredBeforeTheCallerHungUp_KeepsTheAskCharged()
    {
        // K22's cap is only monotone if a timeout is charged. Classifying by "is the caller's token
        // cancelled now" would refund one whenever the caller happens to hang up in the moment after
        // the deadline fired, and a probe that always times out could then offer the same facet
        // forever. The port below makes that order certain: it waits for its own token to be
        // cancelled -- which only the deadline can do here -- and cancels the caller after.
        using CancellationTokenSource caller = new();

        var port = new ProbeFakePort(async (facets, cancellationToken) =>
        {
            if (facets.ContainsKey("applies_to"))
            {
                return [];
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await caller.CancelAsync().ConfigureAwait(false);
                throw;
            }

            throw new UnreachableException();
        });

        var clarificationsObject = new Clarifications();
        using var clarifications = OpenClarifications(clarificationsObject);
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        var knowledge = Resolved(
            ["applies_to"],
            ambiguity: new KnowledgeAmbiguityConfiguration { ProbeDeadlineSeconds = 1 });

        var results = await InvokeSearchWithCallerTokenAsync(Provider(port, knowledge), "e33", caller.Token);

        Assert.Single(results);
        Assert.Contains("holds nothing", results[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, clarificationsObject.Read("applies_to").ProbeAsks);
    }

    [Fact]
    public async Task AThrowBetweenTheProbeSearchAndThePublish_StillResolvesTheLatch()
    {
        // Claiming the latch and publishing an outcome are one contract. Anything that throws between
        // them -- here a log sink, the one call the probe makes into host code after its search
        // returned -- would otherwise leave the payload unresolved, and every other search in the turn
        // would wait out its full margin for an answer that is never coming.
        var port = new ProbeFakePort(facets => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        var clarificationsObject = new Clarifications();
        using var clarifications = OpenClarifications(clarificationsObject);
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        using ThrowingLoggerFactory loggers = new(ProbeRanEventId);
        var provider = KnowledgeProviderFactory.Create(
            port, Resolved(["applies_to"]), "agent-under-test", new SourceLocatorCitationFormatter(), loggers);

        var results = await InvokeSearchAsync(provider, "e33");

        Assert.Single(results);
        Assert.Contains("unreachable", results[0].Text, StringComparison.Ordinal);

        // The latch stays claimed, so the turn cannot probe again -- but it is resolved, so a second
        // call replays at once rather than running out its own clock.
        var loser = clarificationsObject.ClaimProbe();
        Assert.False(loser.Won);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loser.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheProbesOwnSearchIsRecorded_SoAnOperatorSeesBothStoreCalls()
    {
        // The record is what an operator sizes store load and debugs an outage from. A turn in which
        // the probe fires makes two store calls, so it has to file two records -- the second naming
        // the narrowed scope that call actually ran under.
        var port = new ProbeFakePort(facets => Task.FromResult<IReadOnlyList<KnowledgeCard>>(
            facets.ContainsKey("applies_to") ? [] : [CardWithFacet("a", "applies_to", "ct900")]));

        using var clarifications = OpenClarifications();
        using var scope = KnowledgeScopeScope.Open(FullScope("applies_to", "other"));

        using CountingLoggerFactory loggers = new(RetrievedEventId);
        var provider = KnowledgeProviderFactory.Create(
            port, Resolved(["applies_to"]), "agent-under-test", new SourceLocatorCitationFormatter(), loggers);

        var results = await InvokeSearchAsync(provider, "e33");

        Assert.Contains("ct900", results[0].Text, StringComparison.Ordinal);
        Assert.Equal(port.Calls, loggers.Count);
        Assert.Equal(2, loggers.Count);
    }

    // Helpers.

    /// <summary><c>Log.KnowledgeProbeRan</c>'s event id: the probe's one call into host code after its search.</summary>
    private const int ProbeRanEventId = 16;

    private static KnowledgeScope ScopeWithOrigins(
        params (string Facet, string Value, KnowledgeFacetOrigin Origin)[] entries)
    {
        Dictionary<string, string> facets = new(StringComparer.Ordinal);
        Dictionary<string, KnowledgeFacetOrigin> origins = new(StringComparer.Ordinal);

        foreach (var (facet, value, origin) in entries)
        {
            facets[facet] = value;
            origins[facet] = origin;
        }

        return new KnowledgeScope { Facets = facets, Origins = origins };
    }

    /// <summary><c>Log.KnowledgeRetrieved</c>'s event id: one record per store call.</summary>
    private const int RetrievedEventId = 11;

    /// <summary>A logger factory whose loggers count one event id, for a test that sizes store calls.</summary>
    private sealed class CountingLoggerFactory : ILoggerFactory
    {
        private readonly CountingLogger _logger;

        internal CountingLoggerFactory(int eventId) => _logger = new CountingLogger(eventId);

        internal int Count => _logger.Count;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => _logger;

        public void Dispose()
        {
        }

        private sealed class CountingLogger : ILogger
        {
            private readonly int _eventId;

            private int _count;

            internal CountingLogger(int eventId) => _eventId = eventId;

            internal int Count => Volatile.Read(ref _count);

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == _eventId)
                {
                    Interlocked.Increment(ref _count);
                }
            }
        }
    }

    /// <summary>A logger factory whose loggers throw on one event id, standing in for a failing host sink.</summary>
    private sealed class ThrowingLoggerFactory : ILoggerFactory
    {
        private readonly int _eventId;

        internal ThrowingLoggerFactory(int eventId) => _eventId = eventId;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new ThrowingLogger(_eventId);

        public void Dispose()
        {
        }

        private sealed class ThrowingLogger : ILogger
        {
            private readonly int _eventId;

            internal ThrowingLogger(int eventId) => _eventId = eventId;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == _eventId)
                {
                    throw new InvalidOperationException("the log sink is down");
                }
            }
        }
    }

    private static AIContextProvider Provider(IKnowledgeRetrievalPort port, ResolvedKnowledge knowledge)
        => KnowledgeProviderFactory.Create(
            port, knowledge, "agent-under-test", new SourceLocatorCitationFormatter(), loggers: null);

    private static IDisposable OpenClarifications(Clarifications? clarifications = null)
        => TurnAmbientsTestScope.WithClarifications(clarifications ?? new Clarifications());

    private static ResolvedKnowledge Resolved(
        IReadOnlyList<string> fromState,
        string wildcardValue = "*",
        IReadOnlyList<string>? wildcardFacets = null,
        string template = "{key}",
        KnowledgeAmbiguityConfiguration? ambiguity = null,
        IReadOnlyDictionary<string, string?>? slotDescriptions = null,
        bool citations = false,
        bool scoped = true)
        => new(KnowledgeMode.Tool, Limit: 5, Citations: citations, Scoped: scoped)
        {
            Clarification = new ResolvedClarification
            {
                WildcardValue = wildcardValue,
                WildcardFacets = wildcardFacets ?? fromState,
                FromState = fromState,
                SlotDescriptions = slotDescriptions ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                Ambiguity = ambiguity ?? new KnowledgeAmbiguityConfiguration(),
                Template = ScopeTemplate.Parse(template),
            },
        };

    private static Dictionary<string, string?> Descriptions(params (string Slot, string Text)[] entries)
    {
        Dictionary<string, string?> map = new(StringComparer.Ordinal);
        foreach (var (slot, text) in entries)
        {
            map[slot] = text;
        }

        return map;
    }

    private static KnowledgeScope FullScope(params string[] wildcardFacets)
    {
        Dictionary<string, string> facets = new(StringComparer.Ordinal);
        foreach (var facet in wildcardFacets)
        {
            facets[facet] = "*";
        }

        return new KnowledgeScope { Facets = facets };
    }

    private static KnowledgeCard Card(string id)
        => new() { CardId = id, Text = "card " + id, ViaLink = false };

    private static KnowledgeCard CardWithFacet(string id, string facetKey, string value, string template = "{key}")
        => new()
        {
            CardId = id,
            Text = "card " + id,
            ViaLink = false,
            Extras = NestedExtras(template.Replace("{key}", facetKey), value),
        };

    private static KnowledgeCard CardWithFacetList(string id, string facetKey, IReadOnlyList<string> values)
        => new()
        {
            CardId = id,
            Text = "card " + id,
            ViaLink = false,
            Extras = new Dictionary<string, object?>(StringComparer.Ordinal) { [facetKey] = (IReadOnlyList<object?>)[.. values] },
        };

    /// <summary>Builds the nested <c>Extras</c> shape a dotted payload path resolves into.</summary>
    private static IReadOnlyDictionary<string, object?> NestedExtras(string path, string value)
    {
        var parts = path.Split('.');
        object? node = value;

        for (var i = parts.Length - 1; i >= 0; i--)
        {
            node = new Dictionary<string, object?>(StringComparer.Ordinal) { [parts[i]] = node };
        }

        return (IReadOnlyDictionary<string, object?>)node!;
    }

    /// <summary>
    /// Calls the factory's own search delegate directly, exactly as <c>KnowledgeProviderFactoryTests</c>
    /// does.
    /// </summary>
    private static Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> InvokeSearchAsync(
        AIContextProvider provider, string query)
        => RunSearchDelegateAsync(provider, query, TestContext.Current.CancellationToken);

    /// <summary>
    /// The cancellation row's own entry point: <paramref name="cancellationToken"/> here is the
    /// SIMULATED CALLER's own token (K43's cancellation row), never the test host's — see
    /// <see cref="K43_ACancelledProbe_RollsBackTheIncrement_FailsThePayload_AndKeepsTheLatch"/>, its
    /// only caller. Named apart from <see cref="InvokeSearchAsync(AIContextProvider, string)"/> so a
    /// deliberately non-<c>TestContext</c> token at this one call site does not read as a mistake.
    /// </summary>
    private static Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> InvokeSearchWithCallerTokenAsync(
        AIContextProvider provider, string query, CancellationToken callerToken)
        => RunSearchDelegateAsync(provider, query, callerToken);

    private static Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> RunSearchDelegateAsync(
        AIContextProvider provider, string query, CancellationToken cancellationToken)
        => TextSearchProviderInternals.SearchAsync(provider, query, cancellationToken);

    /// <summary>
    /// A knowledge store whose answer is a function of the live scope's facets, so one instance can
    /// stand in for both the main search (full scope) and the probe's own second search (narrowed).
    /// </summary>
    private sealed class ProbeFakePort : IKnowledgeRetrievalPort
    {
        private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyList<KnowledgeCard>>> _answer;

        internal ProbeFakePort(Func<IReadOnlyDictionary<string, string>, Task<IReadOnlyList<KnowledgeCard>>> answer)
            : this((facets, _) => answer(facets))
        {
        }

        internal ProbeFakePort(
            Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyList<KnowledgeCard>>> answer)
            => _answer = answer;

        internal int Calls { get; private set; }

        public async ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
        {
            Calls++;
            var facets = KnowledgeScopeScope.Current?.Facets ?? new Dictionary<string, string>(StringComparer.Ordinal);
            return await _answer(facets, cancellationToken).ConfigureAwait(false);
        }
    }
}

using System.Reflection;
using System.Text.Json;
using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The layer that decides, per agent, how retrieval reaches the model — and the only layer that can
/// enforce the two per-agent settings the one-method port cannot carry.
/// </summary>
/// <remarks>
/// <c>InvokingAsync</c> returns the input context merged with what the provider added, so a test
/// that reads <see cref="AIContext.Messages"/> sees the caller's own message as well. "Injected
/// nothing" is therefore "the input message and no more".
/// </remarks>
public sealed class KnowledgeProviderFactoryTests
{
    [Fact]
    public async Task Create_PrefetchMode_RetrievesBeforeTheModelIsCalled()
    {
        var port = new StubKnowledgePort([Card("a"), Card("b")]);

        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch));
        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        Assert.Null(context.Tools);
        Assert.Equal("the screen says e33", port.LastQuery);
        Assert.Contains("card a", Merged(context), StringComparison.Ordinal);
        Assert.Contains("card b", Merged(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ToolMode_OffersASearchToolInstead()
    {
        var port = new StubKnowledgePort([Card("a")]);

        var provider = Provider(port, Resolved(KnowledgeMode.Tool));
        var context = await provider.InvokingAsync(
            Invoking("hello"), TestContext.Current.CancellationToken);

        Assert.NotNull(context.Tools);
        Assert.Single(context.Tools);
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task Create_PortThrows_InjectsANoticeAndDoesNotFailTheTurn()
    {
        // A16. The framework's own behaviour here is SILENT fail-open: a throwing delegate
        // injects nothing at all and logs nothing, and the model then answers "E03 means
        // overheating" from its own weights. So the delegate must catch and say so.
        var provider = Provider(
            new ThrowingKnowledgePort(new InvalidOperationException("qdrant is down")),
            Resolved(KnowledgeMode.Prefetch));

        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        var text = Merged(context);
        Assert.Contains("knowledge base", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unreachable", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not answer from memory", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_TheStoresOwnDeadlineFires_InjectsTheNoticeAndWritesTheCause()
    {
        // A16 asks this delegate to catch every exception AND its own deadline. The store links that
        // deadline into the caller's token, and the channel reports a cancelled gRPC call as
        // OperationCanceledException -- so a hung Qdrant arrives as the same type a caller cancel
        // does. Excluding the type outright let the deadline straight through: no notice, no record,
        // no log line, and a model answering "E33 means the incline motor" from its own weights.
        RecordingLoggerFactory loggers = new();

        var provider = KnowledgeProviderFactory.Create(
            new HangingKnowledgePort(TimeSpan.FromMilliseconds(20)),
            Resolved(KnowledgeMode.Prefetch),
            "resolver",
            new SourceLocatorCitationFormatter(),
            loggers);

        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        var text = Merged(context);
        Assert.Contains("unreachable", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not answer from memory", text, StringComparison.OrdinalIgnoreCase);

        var line = Assert.Single(loggers.Of(12));
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("resolver", line.Field<string>("Agent"));
        Assert.IsAssignableFrom<OperationCanceledException>(line.Exception);
    }

    [Fact]
    public async Task Create_TheCallerCancels_IsNotDressedUpAsARetrievalFailure()
    {
        // The other half of the same classifier, and the reason it cannot simply catch the type: a
        // hung-up caller produces no answer, so a notice has nowhere to land and an Error row would
        // fire on every abandoned turn of every agent. The port raises its exception carrying the
        // LINKED token here, exactly as the real store does, so nothing but the caller's own token
        // separates this case from the deadline above.
        RecordingLoggerFactory loggers = new();
        using CancellationTokenSource caller = new();

        var provider = KnowledgeProviderFactory.Create(
            new HangingKnowledgePort(TimeSpan.FromMinutes(5)),
            Resolved(KnowledgeMode.Prefetch),
            "resolver",
            new SourceLocatorCitationFormatter(),
            loggers);

        caller.CancelAfter(TimeSpan.FromMilliseconds(20));

        // The framework swallows whatever escapes the delegate, so "propagated" is read off what did
        // NOT happen: no notice, and neither the success row nor the failure row. A delegate that
        // returned normally would have written row 11, and one that took the failure path row 12.
        var context = await provider.InvokingAsync(Invoking("the screen says e33"), caller.Token);

        Assert.DoesNotContain("unreachable", Merged(context), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(loggers.Of(11));
        Assert.Empty(loggers.Of(12));
    }

    [Fact]
    public async Task Create_PortReturnsNothing_InjectsNothing()
    {
        // The score floor is the gate. An empty list must not produce an empty "Additional
        // Context" block that costs tokens and says nothing.
        var provider = Provider(
            new StubKnowledgePort([]), Resolved(KnowledgeMode.Prefetch));

        var context = await provider.InvokingAsync(
            Invoking("hello"), TestContext.Current.CancellationToken);

        Assert.Equal(["hello"], Texts(context));
    }

    [Fact]
    public async Task Create_ScopedAgentWithNoAmbientScope_NeverReachesThePort()
    {
        // Ruling 14(b). The store is shared, so it stays permissive whenever one agent is unscoped.
        // Without this gate, scoped: true on THIS agent would serve every customer every card.
        var port = new StubKnowledgePort([Card("a"), Card("b")]);

        var provider = Provider(
            port, Resolved(KnowledgeMode.Prefetch, scoped: true));
        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        Assert.Equal(0, port.Calls);
        Assert.DoesNotContain("card a", Merged(context), StringComparison.Ordinal);
        Assert.Contains("no scope is open", Merged(context), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not answer from memory", Merged(context), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_ScopedAgentWithAnEmptyScope_NeverReachesThePort()
    {
        // An ambient with no facets filters nothing, so it is the absent ambient wearing a hat.
        var port = new StubKnowledgePort([Card("a"), Card("b")]);
        using var open = KnowledgeScopeScope.Open(new KnowledgeScope { Facets = new Dictionary<string, string>() });

        var provider = Provider(
            port, Resolved(KnowledgeMode.Prefetch, scoped: true));
        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        Assert.Equal(0, port.Calls);
        Assert.Contains("no scope is open", Merged(context), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_ScopedAgentWithAScopeOpen_Searches()
    {
        var port = new StubKnowledgePort([Card("a")]);
        using var open = KnowledgeScopeScope.Open(
            new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } });

        var provider = Provider(
            port, Resolved(KnowledgeMode.Prefetch, scoped: true));
        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        Assert.Equal(1, port.Calls);
        Assert.Contains("card a", Merged(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_UnscopedAgentWithNoAmbientScope_StillSearches()
    {
        // The gate is keyed on the agent's own flag, not on the store's. An unscoped agent in a
        // mixed deployment must keep working when no host opened a scope.
        var port = new StubKnowledgePort([Card("a")]);

        var provider = Provider(
            port, Resolved(KnowledgeMode.Prefetch, scoped: false));
        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        Assert.Equal(1, port.Calls);
        Assert.Contains("card a", Merged(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_UnscopedAgentUnderAScopedHostAmbient_StillSearchesTheWholeCorpus()
    {
        // Ruling 20, and the defect it resolves. example.yaml ships a mixed deployment: the resolver
        // requires a scope, so the host opens ct900 for the whole call, and the analyst -- which the
        // same document says "searches every product on purpose" -- was silently filtered to ct900
        // with it. The store folds whatever ambient it finds into the filter, so the ambient has to
        // stop here.
        var port = new ScopeFilteringKnowledgePort(
            (Card("ct900"), Facets("ct900")),
            (Card("ent"), Facets("ct900ent")));

        using var host = KnowledgeScopeScope.Open(
            new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } });

        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: false));
        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        // The card OUTSIDE the host's facet is the whole point: an unscoped agent sees it.
        Assert.Contains("card ent", Merged(context), StringComparison.Ordinal);
        Assert.Contains("card ct900", Merged(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_UnscopedAgent_ReachesTheStoreUnderAnEmptyScopeRatherThanTheHosts()
    {
        // The mechanism behind the fact above, asserted where the store reads it. An empty facet map
        // adds no filter condition, so this is a whole-corpus read and not a differently-shaped one.
        var port = new StubKnowledgePort([Card("a")]);

        using var host = KnowledgeScopeScope.Open(
            new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } });

        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: false));
        await provider.InvokingAsync(Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        Assert.NotNull(port.ScopeAtTheStore);
        Assert.Empty(port.ScopeAtTheStore.Facets);
    }

    [Fact]
    public async Task Create_ScopedAgent_ReachesTheStoreUnderTheHostsOwnScope()
    {
        // The counterpart. Taking the ambient away from the unscoped agent must not take it away from
        // the scoped one, which is the agent the ambient exists for.
        var port = new StubKnowledgePort([Card("a")]);
        var scope = new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

        using var host = KnowledgeScopeScope.Open(scope);

        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: true));
        await provider.InvokingAsync(Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        Assert.Same(scope, port.ScopeAtTheStore);
    }

    [Fact]
    public async Task Create_UnscopedAgent_PutsTheHostsScopeBackAfterTheSearch()
    {
        // The empty scope covers one port call and nothing else. Leaking it would silently unscope the
        // scoped agent that runs next on the same flow -- the very leak this whole design fails closed
        // against, arriving from the inside.
        var port = new StubKnowledgePort([Card("a")]);
        var scope = new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

        using var host = KnowledgeScopeScope.Open(scope);

        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: false));
        await provider.InvokingAsync(Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        Assert.Same(scope, KnowledgeScopeScope.Current);
    }

    [Fact]
    public async Task Create_ASearchThatAnswered_WritesTheRetrievalRecord()
    {
        // A19. Ruling 21 gave KnowledgeAuditRecord a producer: without one the type shipped tested and
        // dead, and a retrieval left no artifact of any kind.
        RecordingLoggerFactory loggers = new();
        var port = new StubKnowledgePort([Card("a"), Linked("z")]);

        var provider = KnowledgeProviderFactory.Create(
            port, Resolved(KnowledgeMode.Prefetch), "analyst", new SourceLocatorCitationFormatter(), loggers);
        await provider.InvokingAsync(Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        var line = Assert.Single(loggers.Of(11));
        Assert.Equal("analyst", line.Field<string>("Agent"));
        Assert.Equal(2, line.Field<int>("CardCount"));

        var record = line.Field<KnowledgeAuditRecord.LogView>("Record");
        Assert.NotNull(record);
        // "link", not "see_also": the audit record says how the card arrived, in its own words,
        // rather than repeating one collection's payload key back at the reader.
        Assert.Equal(["ranked", "link"], record.Cards.Select(card => card.Via));
        Assert.Equal("analyst", record.Agent);
    }

    [Fact]
    public async Task Create_ASearchThatThrew_WritesTheCauseTheModelNeverSees()
    {
        // In tool mode the framework replaces the message with "Error: Function failed.", and in
        // prefetch mode this delegate answers a notice rather than throwing. Neither channel carries
        // the cause, so an outage during a live call turns on this one line existing.
        RecordingLoggerFactory loggers = new();
        InvalidOperationException down = new("qdrant is down");

        var provider = KnowledgeProviderFactory.Create(
            new ThrowingKnowledgePort(down), Resolved(KnowledgeMode.Prefetch), "resolver", new SourceLocatorCitationFormatter(), loggers);
        await provider.InvokingAsync(Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        var line = Assert.Single(loggers.Of(12));
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("resolver", line.Field<string>("Agent"));

        // The cause travels as the log's own exception argument, which is where a structured sink
        // stores a stack trace. Ruling 22 took the duplicate copy off the record's log view rather
        // than write the same trace into a message field as well.
        Assert.Same(down, line.Exception);
        Assert.Contains("qdrant is down", line.Exception!.ToString(), StringComparison.Ordinal);

        var record = line.Field<KnowledgeAuditRecord.LogView>("Record");
        Assert.NotNull(record);
        Assert.Equal("resolver", record.Agent);
    }

    [Fact]
    public async Task Create_NeitherLoggedRow_CarriesWhatTheCallerSaid()
    {
        // Ruling 22, and a regression this branch introduced. Query is the framework-composed search
        // input: the caller's current utterance plus, at RecentMessageMemoryLimit = 4, up to four
        // earlier messages. The failure row is an Error, which a default production configuration
        // keeps on, so logging the record whole copied every caller's words into a log store once per
        // agent per turn for as long as an outage lasted -- the exact thing Log.PromptRefused and
        // Log.ReplyTruncated refuse a few lines apart in the same file.
        const string spoken = "my ct900 shows e33 and my name is jane quimby on account 40771";

        RecordingLoggerFactory loggers = new();

        var answered = KnowledgeProviderFactory.Create(
            new StubKnowledgePort([Card("a")]), Resolved(KnowledgeMode.Prefetch), "analyst", new SourceLocatorCitationFormatter(), loggers);
        await answered.InvokingAsync(Invoking(spoken), TestContext.Current.CancellationToken);

        var threw = KnowledgeProviderFactory.Create(
            new ThrowingKnowledgePort(new InvalidOperationException("qdrant is down")),
            Resolved(KnowledgeMode.Prefetch),
            "resolver",
            new SourceLocatorCitationFormatter(),
            loggers);
        await threw.InvokingAsync(Invoking(spoken), TestContext.Current.CancellationToken);

        // Both rows were written -- a test that logged nothing would pass the assertions below
        // vacuously, and this is exactly the fact that must not pass vacuously.
        Assert.Single(loggers.Of(11));
        Assert.Single(loggers.Of(12));

        foreach (var line in loggers.Lines)
        {
            // The formatted text AND every structured field: a sink reads the fields, not the string.
            Assert.DoesNotContain(spoken, line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(spoken, line.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);

            foreach (var field in line.Fields)
            {
                Assert.DoesNotContain(
                    spoken, field.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Create_TheLoggedRow_CarriesTheQuerysLengthInstead()
    {
        // What replaces the text, and why a length rather than a hash: an outage is diagnosed by
        // whether the input was well formed. A zero-length query is a provider bug and a runaway one
        // is the recent-message concatenation gone wrong; a length answers both, and a hash answers
        // only "the same text again" while still being a per-caller correlation handle.
        const string spoken = "the screen says e33";

        RecordingLoggerFactory loggers = new();

        var provider = KnowledgeProviderFactory.Create(
            new StubKnowledgePort([Card("a")]), Resolved(KnowledgeMode.Prefetch), "analyst", new SourceLocatorCitationFormatter(), loggers);
        await provider.InvokingAsync(Invoking(spoken), TestContext.Current.CancellationToken);

        var view = Assert.Single(loggers.Of(11)).Field<KnowledgeAuditRecord.LogView>("Record");
        Assert.NotNull(view);
        Assert.Equal(spoken.Length, view.QueryLength);
        Assert.Equal("analyst", view.Agent);
        Assert.Equal(KnowledgeMode.Prefetch, view.Mode);
    }

    [Theory]
    [InlineData(2, "card b", "card c")]
    [InlineData(3, "card c", "card d")]
    public async Task Create_MoreCardsThanTheAgentAsksFor_KeepsTheBestFew(
        int limit, string lastKept, string firstDropped)
    {
        // Ruling 14(c). The store fetches once, up to its own deployment ceiling, for every agent.
        // limit: is this agent's view of that fetch, and the port returns cards best first.
        // Two limits, because one cannot tell the agent's limit from a hardcoded number.
        var port = new StubKnowledgePort([Card("a"), Card("b"), Card("c"), Card("d"), Card("e")]);

        var provider = Provider(
            port, Resolved(KnowledgeMode.Prefetch, limit: limit));
        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        var text = Merged(context);
        Assert.Contains("card a", text, StringComparison.Ordinal);
        Assert.Contains(lastKept, text, StringComparison.Ordinal);
        Assert.DoesNotContain(firstDropped, text, StringComparison.Ordinal);
        Assert.DoesNotContain("card e", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_TheLimit_CountsRankedCardsAndLetsALinkedOneRideAlong()
    {
        // Ruling 16. A7 makes see_also expansion never optional, and the store appends the linked
        // cards after every scored one. A plain prefix would drop every link whenever the fetch
        // filled up -- which, at the shipped defaults of 5 and 5, is every full result. The winning
        // probe arm was "top 5 plus see_also of the top hit": the links are additional to the five.
        var port = new StubKnowledgePort([Card("a"), Card("b"), Card("c"), Linked("z")]);

        var provider = Provider(
            port, Resolved(KnowledgeMode.Prefetch, limit: 2));
        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        var text = Merged(context);
        Assert.Contains("card a", text, StringComparison.Ordinal);
        Assert.Contains("card b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("card c", text, StringComparison.Ordinal);
        Assert.Contains("card z", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_CitationsOff_AsksForNoCitationAtAll()
    {
        // A null CitationsPrompt is not "off": it produces the framework's own default block,
        // which asks for a document name and link, and there is never a link.
        var provider = Provider(
            new StubKnowledgePort([Card("a")]), Resolved(KnowledgeMode.Prefetch, citations: false));

        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        var text = Merged(context);
        Assert.DoesNotContain("Include citations", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ct900-om", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_CitationsOn_NamesTheSourceAndForbidsAnInventedLink()
    {
        var provider = Provider(
            new StubKnowledgePort([Card("a")]), Resolved(KnowledgeMode.Prefetch, citations: true));

        var context = await provider.InvokingAsync(
            Invoking("the screen says e33"), TestContext.Current.CancellationToken);

        var text = Merged(context);
        Assert.Contains("ct900-om, p.27", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Include citations", text, StringComparison.Ordinal);
        Assert.Contains("Do not invent a link", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_CitationsOn_PublishesASourceForEachCardShown()
    {
        // Task 5 fix round 1, Finding 1. Nothing in this suite drove CallSourceScope.Current to a
        // non-null value for a citing search before this test, so the body of the publish loop had
        // never executed. A collector plus an outer call, both open around the same InvokingAsync
        // the other tests already drive, is what proves the wiring rather than just reading it.
        var port = new StubKnowledgePort([Card("a"), Card("b")]);
        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch, citations: true));

        TurnSources sources = new();

        using (TurnAmbientsTestScope.WithSources(sources))
        using (TurnAmbientsTestScope.WithOuterCall("call-1"))
        {
            await provider.InvokingAsync(
                Invoking("the screen says e33"), TestContext.Current.CancellationToken);
        }

        var cited = sources.TakeFor("call-1");
        Assert.Equal(2, cited.Count);
        Assert.Equal(
            ["a", "b"], cited.Select(content => content.Source.SourceId).OrderBy(id => id));
        Assert.All(cited, content => Assert.Equal("ct900-om, p.27", content.Source.Title));
    }

    [Fact]
    public async Task Create_CitationsOff_PublishesNothing()
    {
        // The leak test, and the most important one in this task. citations: false is the single
        // switch a deployment uses to decide whether it discloses its sources at all -- a chip on
        // screen is a disclosure just as much as the label the model is shown -- so this must
        // publish nothing even with a collector and an outer call both open and cards coming back.
        var port = new StubKnowledgePort([Card("a"), Card("b")]);
        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch, citations: false));

        TurnSources sources = new();

        using (TurnAmbientsTestScope.WithSources(sources))
        using (TurnAmbientsTestScope.WithOuterCall("call-1"))
        {
            await provider.InvokingAsync(
                Invoking("the screen says e33"), TestContext.Current.CancellationToken);
        }

        Assert.Empty(sources.TakeFor("call-1"));
    }

    [Fact]
    public async Task Create_MoreCardsThanTheLimit_CitesOnlyTheCardsTheModelIsShown()
    {
        // Finding 2. A card ranked past the agent's limit: is never shown to the model, so citing
        // it anyway would put a chip on screen for a document the answer could not possibly have
        // drawn on. The cited set must match the kept set, not the store's full return.
        var port = new StubKnowledgePort([Card("a"), Card("b"), Card("c"), Card("d"), Card("e")]);
        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch, limit: 2, citations: true));

        TurnSources sources = new();

        using (TurnAmbientsTestScope.WithSources(sources))
        using (TurnAmbientsTestScope.WithOuterCall("call-1"))
        {
            await provider.InvokingAsync(
                Invoking("the screen says e33"), TestContext.Current.CancellationToken);
        }

        var cited = sources.TakeFor("call-1");
        Assert.Equal(2, cited.Count);
        Assert.Equal(
            ["a", "b"], cited.Select(content => content.Source.SourceId).OrderBy(id => id));
    }

    [Fact]
    public async Task Create_WhatAnEarlierProviderInjected_IsNotPartOfTheQuery()
    {
        // The knowledge provider is bound second, after TurnContextProvider, and the framework
        // hands each provider what the one before it produced. The turn's own instructions are not
        // a query, and a search over them retrieves the wrong cards.
        var port = new StubKnowledgePort([]);
        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch));

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        AIContextProvider.InvokingContext context = new(
            StubAgent.Instance,
            session: null,
            new AIContext
            {
                Messages =
                [
                    new ChatMessage(ChatRole.User, "the screen says e33"),
                    new ChatMessage(ChatRole.System, "ask the caller for the machine model")
                        .WithAgentRequestMessageSource(
                            AgentRequestMessageSourceType.AIContextProvider, "TurnContextProvider"),
                ],
            });
#pragma warning restore MAAI001

        await provider.InvokingAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("the screen says e33", port.LastQuery);
    }

    [Fact]
    public async Task Create_ASecondTurn_SearchesWithWhatTheCallerSaidBefore()
    {
        // RecentMessageMemoryLimit defaults to 0, and at 0 turn two's query is the bare new
        // message: "does it need a part?" resolves its pronoun against nothing.
        var port = new StubKnowledgePort([Card("a")]);
        StubSession session = new();

        var provider = Provider(port, Resolved(KnowledgeMode.Prefetch));

        await provider.InvokingAsync(
            Invoking("the screen says e33", session), TestContext.Current.CancellationToken);
        await provider.InvokedAsync(
            Invoked("the screen says e33", session), TestContext.Current.CancellationToken);
        await provider.InvokingAsync(
            Invoking("does it need a part?", session), TestContext.Current.CancellationToken);

        Assert.Contains("the screen says e33", port.LastQuery!, StringComparison.Ordinal);
        Assert.Contains("does it need a part?", port.LastQuery!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_ToolModeWithNoResults_InjectsTheEmptyNotice()
    {
        var provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Tool, scoped: true));
        using var open = KnowledgeScopeScope.Open(
            new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } });

        var results = await InvokeSearchAsync(provider, "f63 error e03");

        Assert.Contains(results, r => r.Text.Contains("holds nothing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_PrefetchModeWithNoResults_InjectsNothing()
    {
        var provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Prefetch));

        var results = await InvokeSearchAsync(provider, "hello");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_ScopedPrefetchModeWithNoResults_InjectsNothing()
    {
        // Both halves of the notice guard matter. A scoped agent with a real facet open already
        // clears the Facets.Count > 0 half, so only the KnowledgeMode.Tool clause keeps a prefetch
        // agent's empty search silent.
        var provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Prefetch, scoped: true));
        using var open = KnowledgeScopeScope.Open(
            new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } });

        var results = await InvokeSearchAsync(provider, "the screen says e33");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_UnscopedToolModeWithNoResults_InjectsNothing()
    {
        // scoped: false opens WholeCorpus, which holds no facets. Such an agent's empty search stays
        // byte-identical to an unscoped one: an empty list, no notice.
        var provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Tool, scoped: false));

        var results = await InvokeSearchAsync(provider, "f63 error e03");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_EveryNotice_CarriesTheReservedSourceName()
    {
        // A notice must never be citable as a card, and the reserved source name is what stops it.
        var provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Tool, scoped: true));
        using var open = KnowledgeScopeScope.Open(
            new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } });

        var results = await InvokeSearchAsync(provider, "f63 error e03");

        Assert.All(results, r => Assert.Equal("agentcore:notice", r.SourceName));
    }

    [Fact]
    public async Task Search_ToolModeThatThrows_StillSaysUnreachable()
    {
        var provider = Provider(
            new ThrowingKnowledgePort(new InvalidOperationException("boom")), Resolved(KnowledgeMode.Tool));

        var results = await InvokeSearchAsync(provider, "f63");

        Assert.Contains(results, r => r.Text.Contains("unreachable", StringComparison.Ordinal));
    }

    /// <summary>Builds the provider under test, with the agent id and logger these facts do not read.</summary>
    /// <param name="port">The store the provider searches.</param>
    /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
    /// <returns>The provider.</returns>
    private static AIContextProvider Provider(IKnowledgeRetrievalPort port, ResolvedKnowledge knowledge)
        => KnowledgeProviderFactory.Create(
            port, knowledge, "agent-under-test", new SourceLocatorCitationFormatter(), loggers: null);

    /// <summary>
    /// Calls the factory's own search delegate directly, rather than through
    /// <see cref="AIContextProvider.InvokingAsync"/>. The guard under test reads
    /// <see cref="ResolvedKnowledge.Mode"/> itself, so it behaves identically whether the framework
    /// would have called the delegate before the model (prefetch) or handed it to the model as a
    /// tool -- <c>TextSearchProvider</c> only decides which of those two happens, and in tool mode
    /// never calls the delegate on its own.
    /// </summary>
    /// <remarks>
    /// Reaches into <c>Microsoft.Agents.AI.TextSearchProvider</c>'s private
    /// <c>Func&lt;string, CancellationToken, Task&lt;IEnumerable&lt;TextSearchResult&gt;&gt;&gt; _searchAsync</c>
    /// field by reflection, verified against <c>Microsoft.Agents.AI</c> 1.17.0. That field is the only
    /// way to reach the delegate uniformly across both <c>SearchTime</c> modes: in
    /// <c>OnDemandFunctionCalling</c> the framework never calls it before the model does, and its own
    /// <c>SearchAsync(string, CancellationToken)</c> wraps it into a formatted string rather than the
    /// raw <see cref="TextSearchProvider.TextSearchResult"/> list these tests need to inspect.
    /// </remarks>
    /// <param name="provider">The provider <see cref="Provider"/> built.</param>
    /// <param name="query">The search text.</param>
    /// <returns>What the delegate returned.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>TextSearchProvider</c> no longer declares the private <c>_searchAsync</c> field this helper
    /// depends on -- a newer <c>Microsoft.Agents.AI</c> changed its internals and this helper needs
    /// updating for the new shape, rather than the caller seeing a bare <see cref="NullReferenceException"/>.
    /// </exception>
    private static async Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> InvokeSearchAsync(
        AIContextProvider provider, string query)
    {
        var field = typeof(TextSearchProvider).GetField("_searchAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "TextSearchProvider no longer has a private field named '_searchAsync'. "
                + "Microsoft.Agents.AI changed its internals; update InvokeSearchAsync for the new shape.");

        var searchAsync = (Func<string, CancellationToken, Task<IEnumerable<TextSearchProvider.TextSearchResult>>>)
            field.GetValue(provider)!;

        return (await searchAsync(query, TestContext.Current.CancellationToken).ConfigureAwait(false)).ToList();
    }

    /// <summary>Every message text of a returned context, in one string.</summary>
    private static string Merged(AIContext context)
        => string.Join('\n', Texts(context));

    private static IEnumerable<string> Texts(AIContext context)
        => (context.Messages ?? []).Select(message => message.Text);

    private static ResolvedKnowledge Resolved(
        KnowledgeMode mode, int limit = 5, bool citations = false, bool scoped = false)
        => new(mode, limit, citations, scoped);

    /// <summary>The facet map one card of the scope-filtering corpus carries.</summary>
    private static Dictionary<string, string> Facets(string model)
        => new(StringComparer.Ordinal) { ["model"] = model };

    private static KnowledgeCard Card(string id)
        => new()
        {
            CardId = id,
            Text = "card " + id,
            Authority = 3,
            SourceRef = "ct900-om",
            SourceLocator = "p.27",
            Score = 0.87,
            ViaLink = false,
        };

    /// <summary>A card <c>see_also</c> pulled in. The store appends these after every scored card.</summary>
    private static KnowledgeCard Linked(string id)
        => Card(id) with { Score = null, ViaLink = true };

    /// <summary>Runs the provider the way the framework runs it, over one caller message.</summary>
    private static AIContextProvider.InvokingContext Invoking(string text, AgentSession? session = null)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        return new(
            StubAgent.Instance,
            session,
            new AIContext { Messages = [new ChatMessage(ChatRole.User, text)] });
#pragma warning restore MAAI001
    }

    /// <summary>Closes a turn, so the provider stores what it should remember of it.</summary>
    private static AIContextProvider.InvokedContext Invoked(string text, AgentSession session)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        return new(
            StubAgent.Instance,
            session,
            [new ChatMessage(ChatRole.User, text)],
            [new ChatMessage(ChatRole.Assistant, "let me look")]);
#pragma warning restore MAAI001
    }

    private sealed class StubSession : AgentSession;

    /// <summary>Stands in for the agent the framework names on a context. Nothing here runs it.</summary>
    private sealed class StubAgent : AIAgent
    {
        public static StubAgent Instance { get; } = new();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default)
            => new(new StubSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

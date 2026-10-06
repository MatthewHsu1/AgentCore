using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Xunit;
using static AgentCore.Application.Tests.Knowledge.KnowledgeProviderFactoryTestSupport;

namespace AgentCore.Application.Tests.Knowledge
{
    public sealed class KnowledgeProviderFactoryCitationTests
    {
        [Fact]
        public async Task Create_CitationsOff_AsksForNoCitationAtAll()
        {
            // A null CitationsPrompt is not "off": it produces the framework's own default block,
            // which asks for a document name and link, and there is never a link.
            AIContextProvider provider = Provider(
                new StubKnowledgePort([Card("a")]), Resolved(KnowledgeMode.Prefetch, citations: false));

            AIContext context = await InvokePrefetchAsync(
                provider, "the screen says e33", PrefetchTurn(), new StubSession());

            string text = Merged(context);
            Assert.DoesNotContain("Include citations", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ct900-om", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_CitationsOn_NamesTheSourceAndForbidsAnInventedLink()
        {
            AIContextProvider provider = Provider(
                new StubKnowledgePort([Card("a")]), Resolved(KnowledgeMode.Prefetch, citations: true));

            AIContext context = await InvokePrefetchAsync(
                provider, "the screen says e33", PrefetchTurn(), new StubSession());

            string text = Merged(context);
            Assert.Contains("ct900-om, p.27", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Include citations", text, StringComparison.Ordinal);
            Assert.Contains("Do not invent a link", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_CitationsOn_PublishesASourceForEachCardShown()
        {
            // This drives the sources port to a non-null value for a citing search,
            // which runs the body of the publish loop. A collector plus an outer call, both on the turn the same InvokingAsync
            // the other tests already drive reads, is what proves the wiring rather than just reading it.
            StubKnowledgePort port = new([Card("a"), Card("b")]);
            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch, citations: true));

            TurnSources sources = new();

            StubSession session = new();
            TurnRegistry.Set(session, PrefetchTurn(sources: sources) with { OuterCallId = "conversation-1" });
            _ = await provider.InvokingAsync(
                Invoking("the screen says e33", session), TestContext.Current.CancellationToken);

            IReadOnlyList<SourceContent> cited = sources.TakeFor("conversation-1");
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
            // publish nothing even with a collector and an outer call both on the turn and cards coming back.
            StubKnowledgePort port = new([Card("a"), Card("b")]);
            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch, citations: false));

            TurnSources sources = new();

            StubSession session = new();
            TurnRegistry.Set(session, PrefetchTurn(sources: sources) with { OuterCallId = "conversation-1" });
            _ = await provider.InvokingAsync(
                Invoking("the screen says e33", session), TestContext.Current.CancellationToken);

            Assert.Empty(sources.TakeFor("conversation-1"));
        }
    }
}

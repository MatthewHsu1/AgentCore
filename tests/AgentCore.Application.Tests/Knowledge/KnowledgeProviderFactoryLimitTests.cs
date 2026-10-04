using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Knowledge.KnowledgeProviderFactoryTestSupport;

namespace AgentCore.Application.Tests.Knowledge
{
    public sealed class KnowledgeProviderFactoryLimitTests
    {
        [Theory]
        [InlineData(2, "card b", "card c")]
        [InlineData(3, "card c", "card d")]
        public async Task Create_MoreCardsThanTheAgentAsksFor_KeepsTheBestFew(
            int limit, string lastKept, string firstDropped)
        {
            // The store fetches once, up to its own deployment ceiling, for every agent.
            // limit: is this agent's view of that fetch, and the port returns cards best first.
            // Two limits, because one cannot tell the agent's limit from a hardcoded number.
            StubKnowledgePort port = new([Card("a"), Card("b"), Card("c"), Card("d"), Card("e")]);

            AIContextProvider provider = Provider(
                port, Resolved(KnowledgeMode.Prefetch, limit: limit));
            AIContext context = await InvokePrefetchAsync(
                provider, "the screen says e33", PrefetchTurn(), new StubSession());

            string text = Merged(context);
            Assert.Contains("card a", text, StringComparison.Ordinal);
            Assert.Contains(lastKept, text, StringComparison.Ordinal);
            Assert.DoesNotContain(firstDropped, text, StringComparison.Ordinal);
            Assert.DoesNotContain("card e", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_TheLimit_CountsRankedCardsAndLetsALinkedOneRideAlong()
        {
            // see_also expansion is never optional, and the store appends the linked
            // cards after every scored one. A plain prefix would drop every link whenever the fetch
            // filled up -- which, at the shipped defaults of 5 and 5, is every full result. The winning
            // probe arm was "top 5 plus see_also of the top hit": the links are additional to the five.
            StubKnowledgePort port = new([Card("a"), Card("b"), Card("c"), Linked("z")]);

            AIContextProvider provider = Provider(
                port, Resolved(KnowledgeMode.Prefetch, limit: 2));
            AIContext context = await InvokePrefetchAsync(
                provider, "the screen says e33", PrefetchTurn(), new StubSession());

            string text = Merged(context);
            Assert.Contains("card a", text, StringComparison.Ordinal);
            Assert.Contains("card b", text, StringComparison.Ordinal);
            Assert.DoesNotContain("card c", text, StringComparison.Ordinal);
            Assert.Contains("card z", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_MoreCardsThanTheLimit_CitesOnlyTheCardsTheModelIsShown()
        {
            // A card ranked past the agent's limit: is never shown to the model, so citing
            // it anyway would put a chip on screen for a document the answer could not possibly have
            // drawn on. The cited set must match the kept set, not the store's full return.
            StubKnowledgePort port = new([Card("a"), Card("b"), Card("c"), Card("d"), Card("e")]);
            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch, limit: 2, citations: true));

            TurnSources sources = new();

            StubSession session = new();
            TurnRegistry.Set(session, PrefetchTurn(sources: sources) with { OuterCallId = "conversation-1" });
            _ = await provider.InvokingAsync(
                Invoking("the screen says e33", session), TestContext.Current.CancellationToken);

            IReadOnlyList<SourceContent> cited = sources.TakeFor("conversation-1");
            Assert.Equal(2, cited.Count);
            Assert.Equal(
                ["a", "b"], cited.Select(content => content.Source.SourceId).OrderBy(id => id));
        }
    }
}

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Knowledge.KnowledgeProviderFactoryTestSupport;

namespace AgentCore.Application.Tests.Knowledge
{
    public sealed class KnowledgeProviderFactoryScopeTests
    {
        [Fact]
        public async Task Create_ScopedAgentWithNoAmbientScope_NeverReachesThePort()
        {
            // Ruling 14(b). The store is shared, so it stays permissive whenever one agent is unscoped.
            // Without this gate, scoped: true on THIS agent would serve every customer every card.
            StubKnowledgePort port = new([Card("a"), Card("b")]);

            AIContextProvider provider = Provider(
                port, Resolved(KnowledgeMode.Prefetch, scoped: true));
            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(), new StubSession());

            Assert.Equal(0, port.Calls);
            Assert.DoesNotContain("card a", Merged(context), StringComparison.Ordinal);
            Assert.Contains("no scope is open", Merged(context), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not answer from memory", Merged(context), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_ScopedAgentWithAnEmptyScope_NeverReachesThePort()
        {
            // An ambient with no facets filters nothing, so it is the absent ambient wearing a hat.
            StubKnowledgePort port = new([Card("a"), Card("b")]);
            KnowledgeScope scope = new() { Facets = new Dictionary<string, string>() };

            AIContextProvider provider = Provider(
                port, Resolved(KnowledgeMode.Prefetch, scoped: true));
            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(scope), new StubSession());

            Assert.Equal(0, port.Calls);
            Assert.Contains("no scope is open", Merged(context), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_ScopedAgentWithAScopeOpen_Searches()
        {
            StubKnowledgePort port = new([Card("a")]);
            KnowledgeScope scope = new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

            AIContextProvider provider = Provider(
                port, Resolved(KnowledgeMode.Prefetch, scoped: true));
            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(scope), new StubSession());

            Assert.Equal(1, port.Calls);
            Assert.Contains("card a", Merged(context), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_UnscopedAgentWithNoAmbientScope_StillSearches()
        {
            // The gate is keyed on the agent's own flag, not on the store's. An unscoped agent in a
            // mixed deployment must keep working when no host opened a scope.
            StubKnowledgePort port = new([Card("a")]);

            AIContextProvider provider = Provider(
                port, Resolved(KnowledgeMode.Prefetch, scoped: false));
            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(), new StubSession());

            Assert.Equal(1, port.Calls);
            Assert.Contains("card a", Merged(context), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_UnscopedAgentUnderAScopedHostAmbient_StillSearchesTheWholeCorpus()
        {
            // Ruling 20, and the defect it resolves. example.yaml ships a mixed deployment: the resolver
            // requires a scope, so the host sets ct900 for the whole conversation, and the analyst -- which the
            // same document says "searches every product on purpose" -- was silently filtered to ct900
            // with it. The store folds whatever scope it is handed into the filter, so the turn's scope
            // has to stop here for an unscoped agent.
            ScopeFilteringKnowledgePort port = new(
                (Card("ct900"), Facets("ct900")),
                (Card("ent"), Facets("ct900ent")));

            KnowledgeScope scope = new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: false));
            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(scope), new StubSession());

            // The card OUTSIDE the host's facet is the whole point: an unscoped agent sees it.
            Assert.Contains("card ent", Merged(context), StringComparison.Ordinal);
            Assert.Contains("card ct900", Merged(context), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_UnscopedAgent_ReachesTheStoreUnderAnEmptyScopeRatherThanTheHosts()
        {
            // The mechanism behind the fact above, asserted where the store reads it. An empty facet map
            // adds no filter condition, so this is a whole-corpus read and not a differently-shaped one.
            StubKnowledgePort port = new([Card("a")]);
            KnowledgeScope scope = new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: false));
            _ = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(scope), new StubSession());

            Assert.NotNull(port.ScopeAtTheStore);
            Assert.Empty(port.ScopeAtTheStore.Facets);
        }

        [Fact]
        public async Task Create_ScopedAgent_ReachesTheStoreUnderTheHostsOwnScope()
        {
            // The counterpart. Taking the scope away from the unscoped agent must not take it away from
            // the scoped one, which is the agent the scope exists for.
            StubKnowledgePort port = new([Card("a")]);
            KnowledgeScope scope = new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: true));
            _ = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(scope), new StubSession());

            Assert.Same(scope, port.ScopeAtTheStore);
        }

        [Fact]
        public async Task Create_UnscopedAgent_PutsTheHostsScopeBackAfterTheSearch()
        {
            // The empty scope covers one port conversation and nothing else. Leaking it would silently unscope the
            // scoped agent that runs next -- the very leak this whole design fails closed against,
            // arriving from the inside.
            StubKnowledgePort port = new([Card("a")]);
            KnowledgeScope scope = new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: false));
            _ = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(), new StubSession());

            // The empty scope covered that one search only: a scoped turn still resolves the host's scope.
            AIContextProvider scoped = Provider(port, Resolved(KnowledgeMode.Prefetch, scoped: true));
            _ = await InvokePrefetchAsync(scoped, "the screen says e33", PrefetchTurn(scope), new StubSession());

            Assert.Same(scope, port.ScopeAtTheStore);
        }

        /// <summary>The facet map one card of the scope-filtering corpus carries.</summary>
        private static Dictionary<string, string> Facets(string model)
        {
            return new(StringComparer.Ordinal) { ["model"] = model };
        }
    }
}

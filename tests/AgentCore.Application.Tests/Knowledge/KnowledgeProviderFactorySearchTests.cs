using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Knowledge.KnowledgeProviderFactoryTestSupport;

namespace AgentCore.Application.Tests.Knowledge
{
    public sealed class KnowledgeProviderFactorySearchTests
    {
        [Fact]
        public async Task Search_ToolModeWithNoResults_InjectsTheEmptyNotice()
        {
            AIContextProvider provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Tool, scoped: true));
            KnowledgeScope scope = new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

            IReadOnlyList<TextSearchProvider.TextSearchResult> results = await InvokeSearchAsync(provider, "f63 error e03", ToolTurn(scope));

            Assert.Contains(results, r => r.Text.Contains("holds nothing", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Search_PrefetchModeWithNoResults_InjectsNothing()
        {
            AIContextProvider provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Prefetch));

            AIContext context = await InvokePrefetchAsync(provider, "hello", PrefetchTurn(), new StubSession());

            Assert.Equal(["hello"], Texts(context));
        }

        [Fact]
        public async Task Search_ScopedPrefetchModeWithNoResults_InjectsNothing()
        {
            // Both halves of the notice guard matter. A scoped agent with a real facet open already
            // clears the Facets.Count > 0 half, so only the KnowledgeMode.Tool clause keeps a prefetch
            // agent's empty search silent.
            AIContextProvider provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Prefetch, scoped: true));
            KnowledgeScope scope = new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(scope), new StubSession());

            Assert.Equal(["the screen says e33"], Texts(context));
        }

        [Fact]
        public async Task Search_UnscopedToolModeWithNoResults_InjectsNothing()
        {
            // scoped: false opens WholeCorpus, which holds no facets. Such an agent's empty search stays
            // byte-identical to an unscoped one: an empty list, no notice.
            AIContextProvider provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Tool, scoped: false));

            IReadOnlyList<TextSearchProvider.TextSearchResult> results = await InvokeSearchAsync(provider, "f63 error e03", ToolTurn());

            Assert.Empty(results);
        }

        [Fact]
        public async Task Search_EveryNotice_CarriesTheReservedSourceName()
        {
            // A notice must never be citable as a card, and the reserved source name is what stops it.
            AIContextProvider provider = Provider(new StubKnowledgePort([]), Resolved(KnowledgeMode.Tool, scoped: true));
            KnowledgeScope scope = new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

            IReadOnlyList<TextSearchProvider.TextSearchResult> results = await InvokeSearchAsync(provider, "f63 error e03", ToolTurn(scope));

            Assert.All(results, r => Assert.Equal("agentcore:notice", r.SourceName));
        }

        [Fact]
        public async Task Search_ToolModeThatThrows_StillSaysUnreachable()
        {
            AIContextProvider provider = Provider(
                new ThrowingKnowledgePort(new InvalidOperationException("boom")), Resolved(KnowledgeMode.Tool));

            IReadOnlyList<TextSearchProvider.TextSearchResult> results = await InvokeSearchAsync(provider, "f63", ToolTurn());

            Assert.Contains(results, r => r.Text.Contains("unreachable", StringComparison.Ordinal));
        }

        private static TurnInvocation ToolTurn(
            KnowledgeScope? scope = null, Clarifications? clarifications = null, TurnSources? sources = null)
        {
            return new()
            {
                ConversationId = "conversation",
                TurnIndex = 0,
                Stage = "",
                Knowledge = scope,
                Clarifications = clarifications ?? new Clarifications(),
                Sources = sources,
            };
        }

        /// <summary>
        /// Invokes the search tool the provider offers, the way the model would: the turn rides along
        /// as an argument, carrying the scope and the clarifications the search runs under.
        /// </summary>
        /// <param name="provider">The provider <see cref="Provider"/> built.</param>
        /// <param name="query">The search text.</param>
        /// <param name="turn">The turn the search runs under.</param>
        /// <returns>What the search returned.</returns>
        private static async Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> InvokeSearchAsync(
            AIContextProvider provider, string query, TurnInvocation turn)
        {
            StubSession session = new();
            AIContext context = await provider.InvokingAsync(
                Invoking("hello", session), TestContext.Current.CancellationToken).ConfigureAwait(false);
            AITool search = Assert.Single(context.Tools!, tool => tool.Name == "Search");
            IReadOnlyList<TextSearchProvider.TextSearchResult>? results = await ((AIFunction)search).InvokeAsync(
                turn.FileIn(new AIFunctionArguments(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["userQuestion"] = query,
                })),
                TestContext.Current.CancellationToken).ConfigureAwait(false)
                as IReadOnlyList<TextSearchProvider.TextSearchResult>;
            return results!;
        }
    }
}

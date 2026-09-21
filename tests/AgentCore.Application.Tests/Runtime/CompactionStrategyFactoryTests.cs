using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="CompactionStrategyFactory"/>: the one fixed <c>Cap -&gt; Summary</c> pair (D7, D9),
    /// fired off the model's window under the token ceiling, and the compiler's refusal when the window is unknown (D14, D15).
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class CompactionStrategyFactoryTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Create_AContextWindowThatIsNotPositive_Throws(int window)
        {
            using SequencedChatClient summariser = new("summary.");

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => CompactionStrategyFactory.Create(summariser, window));
        }

        [Fact]
        public async Task Create_AViewAt76PercentOfTheWindow_FiresAndKeepsOnlyTheFourNewestGroups()
        {
            // window = 1000: fire above 750 (75%). 20 pairs of 76-char messages sum to exactly 3040
            // UTF-8 bytes, which MAF's byte/4 estimator (no tokenizer supplied) reads as exactly 760
            // tokens - over the fire line. There is no token target: every group older than the
            // kept four goes into the one summary.
            using SequencedChatClient summariser = new("summary.");
            CompactionStages stages = CompactionStrategyFactory.Create(summariser, contextWindow: 1000);

            List<ChatMessage> view = Conversation(pairs: 20, charsPerMessage: 76);

            List<ChatMessage> compacted = [.. await CompactionProvider.CompactAsync(
                stages.Summary(stages.Summariser), view, cancellationToken: TestContext.Current.CancellationToken)];

            AssertSummaryThenTail(view, compacted);
        }

        [Fact]
        public async Task Create_AOneMillionWindowAtJustOverTheCeiling_FiresAndKeepsOnlyTheFourNewestGroups()
        {
            // window = 1,000,000: 75% would be 750,000, but the 200,000-token ceiling wins. The
            // estimator rounds down per message, so 20 pairs of 20,004-char messages read as
            // 40 x 5,001 = 200,040 tokens - over the ceiling, far under 75% of the window.
            using SequencedChatClient summariser = new("summary.");
            CompactionStages stages = CompactionStrategyFactory.Create(summariser, contextWindow: 1_000_000);

            List<ChatMessage> view = Conversation(pairs: 20, charsPerMessage: 20_004);

            List<ChatMessage> compacted = [.. await CompactionProvider.CompactAsync(
                stages.Summary(stages.Summariser), view, cancellationToken: TestContext.Current.CancellationToken)];

            AssertSummaryThenTail(view, compacted);
        }

        [Fact]
        public async Task Create_AViewAt74PercentOfTheWindow_ComesBackUnchanged()
        {
            // Same shape, one token under the fire line: 20 pairs of 74-char messages are exactly
            // 2960 bytes, which the byte/4 estimator reads as 740 tokens - at or under 750, so the
            // summary compacts nothing.
            using SequencedChatClient summariser = new("summary.");
            CompactionStages stages = CompactionStrategyFactory.Create(summariser, contextWindow: 1000);

            List<ChatMessage> view = Conversation(pairs: 20, charsPerMessage: 74);

            List<ChatMessage> compacted = [.. await CompactionProvider.CompactAsync(
                stages.Summary(stages.Summariser), view, cancellationToken: TestContext.Current.CancellationToken)];

            Assert.Equal(view.Count, compacted.Count);
            for (int index = 0; index < view.Count; index++)
            {
                Assert.Same(view[index], compacted[index]);
            }
        }

        [Fact]
        public void Compile_AnUnknownModel_FailsNamingTheAgentAtThePointerEndingInModel()
        {
            using SequencedChatClient reply = new("hello there.");

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.CompileAll(
                new AgentCoreConfiguration
                {
                    ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                    Agents = new AgentsConfiguration
                    {
                        Items = [new AgentConfiguration { Id = "only" }],
                    },
                    Entries = new Dictionary<string, EntryConfiguration>
                    {
                        ["main"] = new EntryConfiguration { Agent = "only" },
                    },
                },
                new AgentCompilationContext(new NullWindowChatClientFactory(reply))));

            Assert.Contains("agent 'only'", failure.Message, StringComparison.Ordinal);
            Assert.EndsWith("/model", Assert.Single(failure.Errors).Pointer, StringComparison.Ordinal);
        }

        [Fact]
        public void Compile_EveryAgentOfATwoAgentDocument_HasBothCompactionProviders()
        {
            using SequencedChatClient reply = new("hello there.");

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                new AgentCoreConfiguration
                {
                    ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                    Agents = new AgentsConfiguration
                    {
                        Items =
                        [
                            new AgentConfiguration { Id = "first" },
                            new AgentConfiguration { Id = "second" },
                        ],
                    },
                    Entries = new Dictionary<string, EntryConfiguration>
                    {
                        ["main"] = new EntryConfiguration
                        {
                            Policy = new PolicyConfiguration
                            {
                                Initial = "opening",
                                Stages =
                                [
                                    new StageConfiguration { Id = "opening", Agent = "first" },
                                    new StageConfiguration { Id = "closing", Agent = "second" },
                                ],
                            },
                        },
                    },
                },
                new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"];

            Assert.All(
                compiled.Agents.Values,
                agent =>
                {
                    List<AIContextProvider> providers = [.. Providers(agent)];
                    AIContextProvider cap = Assert.Single(providers, provider => provider is ToolResultCapProvider);
                    AIContextProvider summary = Assert.Single(providers, provider => provider is SummaryRowProvider);
                    Assert.True(providers.IndexOf(cap) < providers.IndexOf(summary), "the cap runs before the summary.");
                });
        }

        /// <summary>One new summary message, then the view's four newest messages, the same instances.</summary>
        private static void AssertSummaryThenTail(List<ChatMessage> view, List<ChatMessage> compacted)
        {
            Assert.Equal(CompactionDefaults.Keep + 1, compacted.Count);
            Assert.DoesNotContain(compacted[0], view);
            for (int index = 1; index < compacted.Count; index++)
            {
                Assert.Same(view[view.Count - CompactionDefaults.Keep + index - 1], compacted[index]);
            }
        }

        private static List<ChatMessage> Conversation(int pairs, int charsPerMessage)
        {
            List<ChatMessage> messages = [];
            for (int index = 0; index < pairs; index++)
            {
                messages.Add(new ChatMessage(ChatRole.User, new string('x', charsPerMessage)));
                messages.Add(new ChatMessage(ChatRole.Assistant, new string('x', charsPerMessage)));
            }

            return messages;
        }

        private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
        {
            ChatClientAgent? inner = agent.GetService<ChatClientAgent>();
            Assert.NotNull(inner);
            return inner.AIContextProviders ?? [];
        }

        /// <summary>Answers no window, as an adapter would for a model id its table does not carry.</summary>
        private sealed class NullWindowChatClientFactory(IChatClient client) : IChatClientFactory
        {
            private readonly IChatClient _client = client;

            public IChatClient GetChatClient(ModelReference? model)
            {
                return _client;
            }

            public int? GetContextWindow(ModelReference? model)
            {
                return null;
            }
        }
    }
#pragma warning restore MAAI001
}

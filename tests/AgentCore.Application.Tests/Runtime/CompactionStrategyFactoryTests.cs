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

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="CompactionStrategyFactory"/>: the one fixed <c>Cap -&gt; Summary</c> pair (D7, D9),
/// sized off the model's window, and the compiler's refusal when the window is unknown (D14, D15).
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

        Assert.Throws<ArgumentOutOfRangeException>(() => CompactionStrategyFactory.Create(summariser, window));
    }

    [Fact]
    public async Task Create_AViewAt76PercentOfTheWindow_FiresAndLandsAtOrUnderTheTarget()
    {
        // window = 1000: fire above 750 (75%), stop at or below 500 (50%). 20 pairs of 76-char
        // messages sum to exactly 3040 UTF-8 bytes, which MAF's byte/4 estimator (no tokenizer
        // supplied) reads as exactly 760 tokens - over the fire line.
        using SequencedChatClient summariser = new("summary.");
        var stages = CompactionStrategyFactory.Create(summariser, contextWindow: 1000);

        var view = Conversation(pairs: 20, charsPerMessage: 76);

        var compacted = (await CompactionProvider.CompactAsync(
            stages.Summary, view, cancellationToken: TestContext.Current.CancellationToken)).ToList();

        var after = EstimatedTokens(compacted);
        Assert.True(after <= 500, $"expected at or under the 500-token target, got {after}.");
    }

    [Fact]
    public async Task Create_AViewAt74PercentOfTheWindow_ComesBackUnchanged()
    {
        // Same shape, one token under the fire line: 20 pairs of 74-char messages are exactly
        // 2960 bytes, which the byte/4 estimator reads as 740 tokens - at or under 750, so the
        // summary compacts nothing.
        using SequencedChatClient summariser = new("summary.");
        var stages = CompactionStrategyFactory.Create(summariser, contextWindow: 1000);

        var view = Conversation(pairs: 20, charsPerMessage: 74);

        var compacted = (await CompactionProvider.CompactAsync(
            stages.Summary, view, cancellationToken: TestContext.Current.CancellationToken)).ToList();

        Assert.Equal(view.Count, compacted.Count);
        for (var index = 0; index < view.Count; index++)
        {
            Assert.Same(view[index], compacted[index]);
        }
    }

    [Fact]
    public void Compile_AnUnknownModel_FailsNamingTheAgentAtThePointerEndingInModel()
    {
        using SequencedChatClient reply = new("hello there.");

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.CompileAll(
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

        var compiled = ConfigurationCompiler.CompileAll(
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
                var providers = Providers(agent).ToList();
                var cap = Assert.Single(providers, provider => provider is ToolResultCapProvider);
                var summary = Assert.Single(providers, provider => provider is SummaryRowProvider);
                Assert.True(providers.IndexOf(cap) < providers.IndexOf(summary), "the cap runs before the summary.");
            });
    }

    private static int EstimatedTokens(IReadOnlyList<ChatMessage> messages)
        => messages.Sum(message => System.Text.Encoding.UTF8.GetByteCount(message.Text)) / 4;

    private static List<ChatMessage> Conversation(int pairs, int charsPerMessage)
    {
        List<ChatMessage> messages = [];
        for (var index = 0; index < pairs; index++)
        {
            messages.Add(new ChatMessage(ChatRole.User, new string('x', charsPerMessage)));
            messages.Add(new ChatMessage(ChatRole.Assistant, new string('x', charsPerMessage)));
        }

        return messages;
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner.AIContextProviders ?? [];
    }

    /// <summary>Answers no window, as an adapter would for a model id its table does not carry.</summary>
    private sealed class NullWindowChatClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;

        public NullWindowChatClientFactory(IChatClient client) => _client = client;

        public IChatClient GetChatClient(ModelReference? model) => _client;

        public int? GetContextWindow(ModelReference? model) => null;
    }
}
#pragma warning restore MAAI001

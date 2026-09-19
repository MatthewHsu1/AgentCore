using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// The <c>compaction:</c> block reaching a compiled agent's context providers, and the trimming
/// reaching the model.
/// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.17.0.
public sealed class CompactionCompilationTests
{
    [Fact]
    public void Compile_AgentWithCompaction_GetsAProvider()
    {
        var providers = Providers(WithCompaction());

        Assert.Contains(providers, provider => provider is CompactionProvider);
    }

    [Fact]
    public void Compile_AgentWithoutCompaction_GetsNoProvider()
    {
        var providers = Providers(WithoutCompaction());

        Assert.DoesNotContain(providers, provider => provider is CompactionProvider);
    }

    [Fact]
    public void Compile_BlockOnOneAgentOnly_GivesOnlyThatAgentAProvider()
    {
        // There is no per-agent opt-out: an omitted block and an explicit null are one value in C#.
        // Selective compaction is expressed by declaring the block per agent instead of on defaults.
        var (withBlock, withoutBlock) = TwoAgentsOneWithCompaction();

        Assert.Contains(withBlock, provider => provider is CompactionProvider);
        Assert.DoesNotContain(withoutBlock, provider => provider is CompactionProvider);
    }

    [Fact]
    public void Compile_TriggerNamesTwoConditions_FailsNamingTheAgentAndThePointer()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => CompileOne(TwoTriggerConditions()));

        Assert.Contains("agent 'only'", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/agents/items/0/compaction/trigger", Assert.Single(failure.Errors).Pointer);
        Assert.Equal(ConfigurationCheck.DocumentSchema, Assert.Single(failure.Errors).Check);
    }

    [Fact]
    public async Task Compile_CompactionBlock_TrimsWhatTheModelSeesAcrossTurns()
    {
        // CompactionStrategyFactoryTests.Create_SameWordsTwiceFromEmptyState_Agrees calls the static
        // CompactionProvider.CompactAsync directly, from empty state, twice — it never opens a session
        // and cannot see the incremental path CompactionProvider actually runs in a compiled agent. This
        // one runs a real compiled agent over several turns of one reused session, and reads back what
        // the chat client was actually sent. Remove the CompactionProvider from
        // AgentContextProviderCompiler.Build and this goes red: the model would then see every message
        // the conversation holds, on every turn.
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.CompileAll(
            new AgentCoreConfiguration
            {
                ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                Agents = new AgentsConfiguration
                {
                    Items =
                    [
                        new AgentConfiguration
                        {
                            Id = "only",
                            Compaction = new CompactionConfiguration
                            {
                                Strategy = CompactionStrategyKind.Truncate,
                                Trigger = new CompactionTriggerConfiguration { Messages = 4 },
                                Keep = 2,
                            },
                        },
                    ],
                },
                Entries = new Dictionary<string, EntryConfiguration>
                {
                    ["main"] = new EntryConfiguration { Agent = "only" },
                },
            },
            new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"];

        var agent = Assert.Single(compiled.Agents.Values);
        var token = TestContext.Current.CancellationToken;
        var session = await agent.CreateSessionAsync(token);

        // AgentCoreChatHistoryProvider (this agent's ChatHistoryProvider, because SingleAgentRow sets
        // SessionCarriesHistory) reads store 1, which nothing here writes to, so it always hands back an
        // empty history and never grows the request on its own. The conversation this test measures is
        // instead built by hand and re-sent whole on every turn — exactly the shape a resumed conversation
        // replays, which is the case CompactionProvider's incremental path exists for.
        List<ChatMessage> conversation = [];
        var sizeBeforeTheLastCompaction = 0;

        const int turns = 6;
        for (var turn = 0; turn < turns; turn++)
        {
            conversation.Add(new ChatMessage(ChatRole.User, $"turn {turn}"));
            sizeBeforeTheLastCompaction = conversation.Count;

            var response = await agent.RunAsync(conversation, session, cancellationToken: token);

            conversation.AddRange(response.Messages);
        }

        var lastRequestSize = reply.Requests[^1].Count;

        Assert.True(
            lastRequestSize < sizeBeforeTheLastCompaction,
            $"the model saw {lastRequestSize} messages on the last turn, of a conversation that held "
            + $"{sizeBeforeTheLastCompaction} before compaction ran.");
    }

    [Fact]
    public async Task Compile_CompactionBlock_KeepsReaderContentOutOfTheStateBag()
    {
        // CompactionProvider copies every message it saw into the session state bag, and the bag
        // serialises with MAF's default options, which know none of AgentCore's content types. A
        // tool-result message carrying a FileContent, RenderContent or SourceContent therefore made
        // the end-of-turn state capture throw NotSupportedException at
        // $.MessageGroups.Messages.Contents, and the compaction state for that turn was lost.
        using SequencedChatClient reply = new("noted.");
        var compiled = CompileCompactingAgent(reply);
        var agent = Assert.Single(compiled.Agents.Values);
        var token = TestContext.Current.CancellationToken;
        var session = await agent.CreateSessionAsync(token);

        ChatMessage published = new(ChatRole.Tool, [new FunctionResultContent("call-1", "published")]);
        published.Contents.Add(new FileContent { Name = "report.md", FileId = "out/report.md", Kept = true });
        published.Contents.Add(new RenderContent
        {
            Name = "card",
            RenderId = "card-1",
            Data = JsonSerializer.SerializeToElement(new { title = "Report" }),
        });
        published.Contents.Add(new SourceContent
        {
            Source = new SourceReference { SourceId = "doc-1", Kind = SourceKind.Document, Title = "Doc", Origin = "knowledge" },
            CallId = "call-1",
        });
        List<ChatMessage> conversation =
        [
            new ChatMessage(ChatRole.User, "make me a report"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "file.publish")]),
            published,
            new ChatMessage(ChatRole.User, "thanks"),
        ];

        await agent.RunAsync(conversation, session, cancellationToken: token);

        // Capture serialises the whole bag before it picks keys, so any non-empty key set walks
        // past the compaction state. The harness keys are empty here: no harness: block.
        var compactionKey = Assert.Single(Assert.Single(Providers(agent).OfType<CompactionProvider>()).StateKeys);
        var captured = HarnessSessionState.Capture(session, new HashSet<string>([compactionKey], StringComparer.Ordinal));

        Assert.Contains(compactionKey, captured.Keys);
        Assert.DoesNotContain(
            reply.Requests[^1].SelectMany(message => message.Contents),
            content => content is FileContent or RenderContent or SourceContent);
    }

    private static CompiledAgent CompileCompactingAgent(SequencedChatClient reply) => ConfigurationCompiler.CompileAll(
        new AgentCoreConfiguration
        {
            ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
            Agents = new AgentsConfiguration
            {
                Items =
                [
                    new AgentConfiguration
                    {
                        Id = "only",
                        Compaction = new CompactionConfiguration
                        {
                            Strategy = CompactionStrategyKind.Truncate,
                            Trigger = new CompactionTriggerConfiguration { Messages = 4 },
                            Keep = 2,
                        },
                    },
                ],
            },
            Entries = new Dictionary<string, EntryConfiguration>
            {
                ["main"] = new EntryConfiguration { Agent = "only" },
            },
        },
        new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"];

    private static AgentCoreConfiguration TwoTriggerConditions() => new()
    {
        ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
        Entries = new Dictionary<string, EntryConfiguration> { ["main"] = new EntryConfiguration { Agent = "only" } },
        Agents = new AgentsConfiguration
        {
            Items =
            [
                new AgentConfiguration
                {
                    Id = "only",
                    Compaction = new CompactionConfiguration
                    {
                        Strategy = CompactionStrategyKind.ToolResult,
                        Trigger = new CompactionTriggerConfiguration { Tokens = 60000, Messages = 40 },
                    },
                },
            ],
        },
    };

    private static AIAgent WithCompaction() => CompileOne(new AgentCoreConfiguration
    {
        ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
        Entries = new Dictionary<string, EntryConfiguration> { ["main"] = new EntryConfiguration { Agent = "only" } },
        Agents = new AgentsConfiguration
        {
            Items =
            [
                new AgentConfiguration
                {
                    Id = "only",
                    Compaction = Block(),
                },
            ],
        },
    });

    private static AIAgent WithoutCompaction() => CompileOne(new AgentCoreConfiguration
    {
        ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
        Entries = new Dictionary<string, EntryConfiguration> { ["main"] = new EntryConfiguration { Agent = "only" } },
        Agents = new AgentsConfiguration
        {
            Items = [new AgentConfiguration { Id = "only" }],
        },
    });

    private static (IEnumerable<AIContextProvider> WithBlock, IEnumerable<AIContextProvider> WithoutBlock)
        TwoAgentsOneWithCompaction()
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
                        new AgentConfiguration { Id = "withBlock", Compaction = Block() },
                        new AgentConfiguration { Id = "withoutBlock" },
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
                                new StageConfiguration { Id = "opening", Agent = "withBlock" },
                                new StageConfiguration { Id = "closing", Agent = "withoutBlock" },
                            ],
                        },
                    },
                },
            },
            new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"];

        return (Providers(compiled.Agents["withBlock"]), Providers(compiled.Agents["withoutBlock"]));
    }

    private static CompactionConfiguration Block() => new()
    {
        Strategy = CompactionStrategyKind.ToolResult,
        Trigger = new CompactionTriggerConfiguration { Tokens = 60000 },
    };

    private static AIAgent CompileOne(AgentCoreConfiguration configuration)
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.CompileAll(
            configuration,
            new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"];

        return Assert.Single(compiled.Agents.Values);
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner.AIContextProviders ?? [];
    }
}
#pragma warning restore MAAI001

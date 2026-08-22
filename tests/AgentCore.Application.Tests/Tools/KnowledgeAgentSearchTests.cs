using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tests.Tools.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Shipped;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// <c>knowledge.agent_search</c> runs multi-hop search over the knowledge ports and hands the outer
/// agent one answer. These tests hold the parts a reader cannot check by eye: that the instructions
/// and the inner tools name the same four things, and that the intermediate calls stay inside.
/// </summary>
public sealed class KnowledgeAgentSearchTests
{
    /// <summary>
    /// The instructions introduce the inner tools in prose and nothing at compile time joins the
    /// two. It asserts the bullet form rather than the bare name because <c>search</c> and
    /// <c>read</c> both recur as ordinary English elsewhere in the prose, so a bare substring would
    /// still pass with their bullets deleted — a guard that cannot fail for half the tools it
    /// covers.
    /// </summary>
    [Fact]
    public void Text_IntroducesEveryInnerToolByName()
    {
        Assert.All(
            KnowledgeAgentTools.Names,
            name => Assert.Contains($"- `{name}`", SearchVocabulary.Text, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>list</c> and <c>grep</c> answer with a <c>truncated</c> field and the instructions tell
    /// the agent to react to it by name. Renaming that field without editing the prose would send
    /// the model looking for something that is not there, and a cut-off result would read to it as
    /// an empty knowledge base.
    /// </summary>
    [Fact]
    public void Text_NamesTheTruncatedFieldTheToolsReturn()
    {
        Assert.Contains("truncated", SearchVocabulary.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(SearchVocabulary.Text));
    }

    private static ToolConfiguration Declared() => new()
    {
        Id = "search_files",
        Kind = ToolKind.Builtin,
        Uses = BuiltinToolNames.KnowledgeAgentSearch,
    };

    private static ValueTask<IReadOnlyList<ToolRegistration>> Provide(BuiltinToolPorts ports)
        => new BuiltinToolSource(ports).ProvideAsync(
            new ToolSourceContext(new AgentCoreConfiguration
            {
                ApiVersion = "agentcore/v1",
                Name = "test",
                Tools = [Declared()],
            }),
            TestContext.Current.CancellationToken);

    [Fact]
    public void Definition_UsesTheNameADocumentWrites()
    {
        Assert.Equal("knowledge.agent_search", BuiltinToolNames.KnowledgeAgentSearch);
        Assert.Equal(BuiltinToolNames.KnowledgeAgentSearch, new KnowledgeAgentSearchDefinition().Name);
    }

    [Fact]
    public void Definition_InstructionsAreTheVocabulary()
    {
        Assert.Equal(SearchVocabulary.Text, new KnowledgeAgentSearchDefinition().Instructions);
    }

    [Fact]
    public void Definition_InnerToolsAreTheFourKnowledgeTools()
    {
        var port = new MapKnowledgePort();

        var tools = new KnowledgeAgentSearchDefinition()
            .InnerTools(Declared(), new BuiltinToolPorts(port, port, null));

        Assert.Equal(KnowledgeAgentTools.Names, tools.OfType<AIFunction>().Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public void Definition_NoRetrievalPort_NamesThatPort()
    {
        var port = new MapKnowledgePort();

        Assert.Equal(
            nameof(IKnowledgeRetrievalPort),
            new KnowledgeAgentSearchDefinition().MissingPort(new BuiltinToolPorts(null, port, null)));
    }

    [Fact]
    public void Definition_NoDocumentStorePort_NamesThatPort()
    {
        var port = new MapKnowledgePort();

        Assert.Equal(
            nameof(IDocumentStorePort),
            new KnowledgeAgentSearchDefinition().MissingPort(new BuiltinToolPorts(port, null, null)));
    }

    [Fact]
    public void Definition_BothPortsBound_NamesNoMissingPort()
    {
        var port = new MapKnowledgePort();

        Assert.Null(new KnowledgeAgentSearchDefinition().MissingPort(new BuiltinToolPorts(port, port, null)));
    }

    /// <summary>
    /// It reaches the shipped-agent table, not the plain-function one. Landing in the wrong table
    /// would boot clean and then reject the <c>maxRounds:</c> the spec's own example writes.
    /// </summary>
    [Fact]
    public async Task ProvideAsync_ADocumentDeclaresIt_ServesItAsAShippedAgent()
    {
        var port = new MapKnowledgePort();

        var registrations = await Provide(
            new BuiltinToolPorts(port, port, new RecordingChatClientFactory()));

        var registration = Assert.Single(registrations);
        Assert.Equal("search_files", registration.Id);
        Assert.False(string.IsNullOrWhiteSpace(registration.Description));
    }

    [Fact]
    public async Task ProvideAsync_NoKnowledgePort_FailsTheBootNamingTheToolAndThePort()
    {
        var error = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Provide(new BuiltinToolPorts(null, null, new RecordingChatClientFactory())));

        Assert.Contains("search_files", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IKnowledgeRetrievalPort), error.Message, StringComparison.Ordinal);
    }

    private static readonly string[] BeltThenRollersQueries = ["belt tension", "rear roller bolts"];

    private static AIFunction BuildAgent(MapKnowledgePort port, IChatClient client, int? maxRounds = null)
        => ShippedAgentBuilder.Build(
            new KnowledgeAgentSearchDefinition(),
            maxRounds is { } rounds ? Declared() with { MaxRounds = rounds } : Declared(),
            new BuiltinToolPorts(port, port, new RecordingChatClientFactory(client)));

    /// <summary>
    /// The reason this is an agent and not a bigger function: it searches, reads what it found, and
    /// searches again. A single-call tool cannot do the second hop, because the second query is
    /// written from the first result.
    /// </summary>
    [Fact]
    public async Task Invoke_TheModelSearchesThenReadsThenSearchesAgain_EveryHopReachesThePorts()
    {
        var port = new MapKnowledgePort()
            .With("f63/belt.md", "The belt tension is set by the rear roller bolts.")
            .With("f63/rollers.md", "Turn each rear roller bolt a quarter turn.");

        var client = new ScriptedToolCallingChatClient(
            ("search", """{"query":"belt tension"}"""),
            ("read", """{"documentId":"f63/belt.md"}"""),
            ("search", """{"query":"rear roller bolts"}"""))
        {
            FinalText = "Turn each rear roller bolt a quarter turn. See f63/rollers.md.",
        };

        var result = await BuildAgent(port, client).InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "how do I tension the belt" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(BeltThenRollersQueries, port.Queries.ToArray());
        Assert.Contains("quarter turn", Describe(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// Decision 16's whole point. The outer agent is handed the answer and nothing else: not the
    /// queries, not the document ids the inner agent opened, not the passages it rejected. If any of
    /// those leaked, the context isolation that justifies a second search tool would not exist.
    /// </summary>
    [Fact]
    public async Task Invoke_TheInnerAgentReadsSeveralDocuments_TheOuterAgentSeesOnlyTheAnswer()
    {
        var port = new MapKnowledgePort()
            .With("f63/belt.md", "SECRET_PASSAGE_ONE the belt tension is set by the rear roller bolts.")
            .With("f63/rollers.md", "SECRET_PASSAGE_TWO turn each rear roller bolt a quarter turn.");

        var client = new ScriptedToolCallingChatClient(
            ("search", """{"query":"belt"}"""),
            ("read", """{"documentId":"f63/rollers.md"}"""))
        {
            FinalText = "Turn each rear roller bolt a quarter turn.",
        };

        var answer = Describe(await BuildAgent(port, client).InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "belt" }),
            TestContext.Current.CancellationToken));

        Assert.Equal("Turn each rear roller bolt a quarter turn.", answer);
        Assert.DoesNotContain("SECRET_PASSAGE", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The knowledge ports document that an adapter may throw. Task 1 put the auditing loop under a
    /// shipped agent so that a fault the model can answer becomes a result it reads. Here the store
    /// refuses one query, and the agent still answers rather than ending the outer turn.
    /// </summary>
    [Fact]
    public async Task Invoke_TheKnowledgeAdapterThrowsAFaultTheModelCanAnswer_TheAgentStillAnswers()
    {
        var port = new MapKnowledgePort { Failure = new InvalidOperationException("the index is rebuilding") };

        var client = new ScriptedToolCallingChatClient(("search", """{"query":"belt"}"""))
        {
            FinalText = "I could not search the knowledge base just now.",
        };

        var answer = Describe(await BuildAgent(port, client).InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "belt" }),
            TestContext.Current.CancellationToken));

        Assert.Contains("could not search", answer, StringComparison.Ordinal);
    }

    /// <summary>Reads the words out of whatever shape <c>AsAIFunction</c> handed back.</summary>
    private static string Describe(object? result) => result switch
    {
        string text => text,
        System.Text.Json.JsonElement element => element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.ToString(),
        _ => result?.ToString() ?? string.Empty,
    };
}

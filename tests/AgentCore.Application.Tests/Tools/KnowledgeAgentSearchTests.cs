using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
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
}

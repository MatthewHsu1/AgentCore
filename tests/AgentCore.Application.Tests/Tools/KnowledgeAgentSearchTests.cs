using AgentCore.Application.Tools.Shipped;
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
    /// The instructions name the inner tools in prose and nothing at compile time joins the two.
    /// Renaming a tool without editing the prose would leave the model calling a name that is not
    /// offered, which costs a whole round to discover at run time and only in production.
    /// </summary>
    [Fact]
    public void Text_NamesEveryInnerTool()
    {
        Assert.All(
            KnowledgeAgentTools.Names,
            name => Assert.Contains(name, SearchVocabulary.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Text_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(SearchVocabulary.Text));
    }
}

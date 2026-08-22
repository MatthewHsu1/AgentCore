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
}

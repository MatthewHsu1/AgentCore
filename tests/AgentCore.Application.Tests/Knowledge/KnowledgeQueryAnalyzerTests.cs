using AgentCore.Application.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

public sealed class NoQueryAnalyzerTests
{
    private static readonly NoQueryAnalyzer Analyzer = new();

    [Fact]
    public void Name_IsTheConfigurationValue() => Assert.Equal("none", Analyzer.Name);

    [Fact]
    public void RequiredTerms_AlwaysEmpty()
        => Assert.Empty(Analyzer.RequiredTerms("the screen says e33"));

    [Fact]
    public void RequiredTerms_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => Analyzer.RequiredTerms(null!));
}

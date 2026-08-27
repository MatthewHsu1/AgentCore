using AgentCore.Application.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

public sealed class IdentifierCodeAnalyzerTests
{
    private static readonly IdentifierCodeAnalyzer Analyzer = new();

    [Fact]
    public void Name_IsTheConfigurationValue() => Assert.Equal("identifier-codes", Analyzer.Name);

    [Theory]
    [InlineData("the screen says e33", "e33")]
    [InlineData("THE SCREEN SAYS E33", "e33")]
    [InlineData("error ol1 on startup", "ol1")]
    [InlineData("code ce10 keeps coming back", "ce10")]
    public void RequiredTerms_FindsTheIdentifier(string query, string expected)
        => Assert.Equal([expected], Analyzer.RequiredTerms(query));

    [Fact]
    public void RequiredTerms_TwoIdentifiers_ReturnsBothInOrder()
        => Assert.Equal(["e33", "e27"], Analyzer.RequiredTerms("the screen says e33 e27"));

    [Fact]
    public void RequiredTerms_RepeatedIdentifier_IsReturnedOnce()
        => Assert.Equal(["e33"], Analyzer.RequiredTerms("e33 again, still e33"));

    [Theory]
    [InlineData("how do i clean the deck")]
    [InlineData("")]
    [InlineData("treadmill")]
    // Five letters, then digits: too long to be a console code.
    [InlineData("model ct9000x")]
    // Digits with no leading letters.
    [InlineData("part 90210")]
    public void RequiredTerms_NoIdentifier_IsEmpty(string query)
        => Assert.Empty(Analyzer.RequiredTerms(query));

    [Fact]
    public void RequiredTerms_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => Analyzer.RequiredTerms(null!));
}

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

using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The one place a card becomes what the framework injects. <c>citations</c> is applied here, and
/// nowhere else.
/// </summary>
public sealed class KnowledgeCardMapperTests
{
    [Fact]
    public void ToResult_CitationsOff_SendsNoSourceName()
    {
        // The model cannot leak a label it never received. Manifest titles end in
        // "(curated notes)" and ticket titles can carry a customer name.
        var result = KnowledgeCardMapper.ToResult(Card(), citations: false);

        Assert.Null(result.SourceName);
        Assert.Null(result.SourceLink);
        Assert.Equal("the body", result.Text);
    }

    [Fact]
    public void ToResult_CitationsOn_NamesTheSourceButStillNoLink()
    {
        // source.ref is a manifest id and source.locator is "p.27". Neither is a URL, and a
        // synthesised kb:// scheme would invite someone to click it.
        var result = KnowledgeCardMapper.ToResult(Card(), citations: true);

        Assert.Equal("ct900-om, p.27", result.SourceName);
        Assert.Null(result.SourceLink);
    }

    [Fact]
    public void ToResult_AlwaysCarriesTheWholeCard()
    {
        // TextSearchResult has no Score field, so the audit record reads it from here.
        var card = Card();

        Assert.Same(card, KnowledgeCardMapper.ToResult(card, citations: false).RawRepresentation);
    }

    [Fact]
    public void ToResult_CitationsOn_BothRefAndLocator_JoinsWithComma()
    {
        var card = Card() with { SourceRef = "ct900-om", SourceLocator = "p.27" };

        var result = KnowledgeCardMapper.ToResult(card, citations: true);

        Assert.Equal("ct900-om, p.27", result.SourceName);
    }

    [Fact]
    public void ToResult_CitationsOn_OnlyRef_OmitsTheSeparator()
    {
        var card = Card() with { SourceRef = "ct900-om", SourceLocator = string.Empty };

        var result = KnowledgeCardMapper.ToResult(card, citations: true);

        Assert.Equal("ct900-om", result.SourceName);
    }

    [Fact]
    public void ToResult_CitationsOn_OnlyLocator_OmitsTheSeparator()
    {
        var card = Card() with { SourceRef = string.Empty, SourceLocator = "p.27" };

        var result = KnowledgeCardMapper.ToResult(card, citations: true);

        Assert.Equal("p.27", result.SourceName);
    }

    [Fact]
    public void ToResult_CitationsOn_NeitherRefNorLocator_SendsNoSourceName()
    {
        var card = Card() with { SourceRef = string.Empty, SourceLocator = string.Empty };

        var result = KnowledgeCardMapper.ToResult(card, citations: true);

        Assert.Null(result.SourceName);
    }

    private static KnowledgeCard Card()
        => new()
        {
            CardId = "ct900-e33-incline-err",
            Text = "the body",
            Authority = 3,
            SourceRef = "ct900-om",
            SourceLocator = "p.27",
            Score = 0.87,
            ViaLink = false,
        };
}

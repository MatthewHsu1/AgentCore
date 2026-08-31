using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using AgentCore.Domain.Sources;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The one place a card becomes the chip the caller sees.
/// </summary>
/// <remarks>
/// The title is the same label the model was shown. Two wordings of one citation — one for the
/// model, another for the screen — is a way for the two to disagree in front of a customer, so
/// there is one formatter and this reads it.
/// </remarks>
public sealed class KnowledgeSourceMapperTests
{
    private static readonly IKnowledgeCitationFormatter Formatter = new SourceLocatorCitationFormatter();

    [Fact]
    public void ToSource_ReadsTheFormattersLabelAsTheTitle()
    {
        var source = KnowledgeSourceMapper.ToSource(Card(), Formatter);

        Assert.NotNull(source);
        Assert.Equal("ct900-om, p.27", source!.Title);
        Assert.Equal("card-1", source.SourceId);
        Assert.Equal(SourceKind.Document, source.Kind);
        Assert.Equal("knowledge", source.Origin);
        Assert.Equal("p.27", source.Locator);
        Assert.Null(source.Url);
    }

    [Fact]
    public void ToSource_ACardTheFormatterCitesNothingFor_IsNotACitation()
    {
        // A collection that maps neither source nor locator cites nothing. A chip reading "card-1"
        // would show the caller an internal id and tell them nothing.
        var card = Card() with { SourceRef = "", SourceLocator = "" };

        Assert.Null(KnowledgeSourceMapper.ToSource(card, Formatter));
    }

    [Fact]
    public void ToSource_NeverInventsALink()
    {
        // A card has no URL. SourceLocatorCitationFormatter's whole point is that a synthesised
        // kb:// scheme would invite a click that goes nowhere.
        var source = KnowledgeSourceMapper.ToSource(Card(), Formatter);

        Assert.Null(source!.Url);
        Assert.Equal(SourceKind.Document, source.Kind);
    }

    private static KnowledgeCard Card() => new()
    {
        CardId = "card-1",
        Text = "the body",
        ViaLink = false,
        SourceRef = "ct900-om",
        SourceLocator = "p.27",
    };
}

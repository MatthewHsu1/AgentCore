using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The one place a card becomes what the framework injects. <c>citations</c> is applied here, and
/// nowhere else.
/// </summary>
/// <remarks>
/// The wording of a label is no longer this class's business: it belongs to whichever
/// <see cref="IKnowledgeCitationFormatter"/> the document names. What is still tested here is the
/// switch — on or off — and that a blank label is treated as no label.
/// </remarks>
public sealed class KnowledgeCardMapperTests
{
    [Fact]
    public void ToResult_CitationsOff_SendsNoSourceName()
    {
        // The model cannot leak a label it never received. Manifest titles end in
        // "(curated notes)" and ticket titles can carry a customer name.
        var result = KnowledgeCardMapper.ToResult(Card(), citations: false, Formatter);

        Assert.Null(result.SourceName);
        Assert.Null(result.SourceLink);
        Assert.Equal("the body", result.Text);
    }

    [Fact]
    public void ToResult_CitationsOn_NamesTheSourceButStillNoLink()
    {
        // source.ref is a manifest id and source.locator is "p.27". Neither is a URL, and a
        // synthesised kb:// scheme would invite someone to click it.
        var result = KnowledgeCardMapper.ToResult(Card(), citations: true, Formatter);

        Assert.Equal("ct900-om, p.27", result.SourceName);
        Assert.Null(result.SourceLink);
    }

    [Fact]
    public void ToResult_AlwaysCarriesTheWholeCard()
    {
        // TextSearchResult has no Score field, so the audit record reads it from here.
        var card = Card();

        Assert.Same(card, KnowledgeCardMapper.ToResult(card, citations: false, Formatter).RawRepresentation);
    }

    [Fact]
    public void ToResult_CitationsOn_BothRefAndLocator_JoinsWithComma()
    {
        var card = Card() with { SourceRef = "ct900-om", SourceLocator = "p.27" };

        var result = KnowledgeCardMapper.ToResult(card, citations: true, Formatter);

        Assert.Equal("ct900-om, p.27", result.SourceName);
    }

    [Fact]
    public void ToResult_CitationsOn_OnlyRef_OmitsTheSeparator()
    {
        var card = Card() with { SourceRef = "ct900-om", SourceLocator = string.Empty };

        var result = KnowledgeCardMapper.ToResult(card, citations: true, Formatter);

        Assert.Equal("ct900-om", result.SourceName);
    }

    [Fact]
    public void ToResult_CitationsOn_OnlyLocator_OmitsTheSeparator()
    {
        var card = Card() with { SourceRef = string.Empty, SourceLocator = "p.27" };

        var result = KnowledgeCardMapper.ToResult(card, citations: true, Formatter);

        Assert.Equal("p.27", result.SourceName);
    }

    [Fact]
    public void ToResult_CitationsOn_NeitherRefNorLocator_SendsNoSourceName()
    {
        var card = Card() with { SourceRef = string.Empty, SourceLocator = string.Empty };

        var result = KnowledgeCardMapper.ToResult(card, citations: true, Formatter);

        Assert.Null(result.SourceName);
    }

    [Fact]
    public void ToResult_CitationsOn_AFormatterThatWritesNothing_SendsNoSourceName()
    {
        // A bare separator in front of the model is worse than no citation, so an empty answer and
        // a null answer have to mean the same thing here.
        var result = KnowledgeCardMapper.ToResult(Card(), citations: true, new BlankFormatter());

        Assert.Null(result.SourceName);
    }

    [Fact]
    public void ToResult_CitationsOn_AFormatterReadingExtras_ShowsWhatItRead()
    {
        // The point of the seam: a label built from a field no KnowledgeCard property names.
        var card = Card() with
        {
            Extras = new Dictionary<string, object?>(StringComparer.Ordinal) { ["revision"] = "rev C" },
        };

        var result = KnowledgeCardMapper.ToResult(card, citations: true, new ExtrasFormatter());

        Assert.Equal("rev C", result.SourceName);
    }

    /// <summary>The shipped wording, which every test above that does not say otherwise uses.</summary>
    private static SourceLocatorCitationFormatter Formatter => new();

    /// <summary>A formatter that answers with whitespace, which must read as no citation.</summary>
    private sealed class BlankFormatter : IKnowledgeCitationFormatter
    {
        public string Name => "blank";

        public string? Format(KnowledgeCard card) => "   ";
    }

    /// <summary>A formatter that cites a payload field no property on the card names.</summary>
    private sealed class ExtrasFormatter : IKnowledgeCitationFormatter
    {
        public string Name => "extras";

        public string? Format(KnowledgeCard card) => card.Extras["revision"] as string;
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

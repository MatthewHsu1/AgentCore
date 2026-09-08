using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// The prefixed-sibling narrowing rule: what the caller spelled after the matched value decides
/// which of its longer siblings are still plausible. A sibling whose own extension is a peer of the
/// caller's — same length, same character class — and is not the one the caller said, is ruled out
/// by the caller's own words rather than offered back as a near-tie.
/// </summary>
public sealed class ExactStateValueLinkerSiblingNarrowingTests
{
    private static readonly HashSet<string> NothingNamed = new(StringComparer.Ordinal);

    private static readonly string[] DatedCatalogue = ["F63", "F63-2013", "F63-2023", "F63-2026"];

    private static readonly ExactStateValueLinker Linker = new();

    [Fact]
    public void Link_ExtensionIsAPeerOfEverySibling_LinksTheBareValue()
    {
        var result = Linker.Link("f63 2016", Vocabulary(DatedCatalogue), NothingNamed);

        AssertLinked(result, "F63");
    }

    [Fact]
    public void Link_ExtensionIsAPeerOfOnlySomeSiblings_KeepsTheRest()
    {
        var result = Linker.Link("f63 2016", Vocabulary("F63", "F63-2013", "F63-2016B"), NothingNamed);

        AssertAmbiguous(result, "F63", "F63-2016B");
    }

    [Fact]
    public void Link_ExtensionOfADifferentCharacterClass_DecidesNothing()
    {
        var result = Linker.Link("f63 console", Vocabulary(DatedCatalogue), NothingNamed);

        AssertAmbiguous(result, "F63", "F63-2013", "F63-2023", "F63-2026");
    }

    [Fact]
    public void Link_ExtensionOfADifferentLength_DecidesNothing()
    {
        var result = Linker.Link("a CT800 please", Vocabulary("ct800", "ct800ent"), NothingNamed);

        AssertAmbiguous(result, "ct800", "ct800ent");
    }

    [Fact]
    public void Link_NoExtensionAtAll_LeavesEverySiblingStanding()
    {
        var result = Linker.Link("f63", Vocabulary(DatedCatalogue), NothingNamed);

        AssertAmbiguous(result, "F63", "F63-2013", "F63-2023", "F63-2026");
    }

    [Fact]
    public void Link_ExtensionThatStartsASibling_KeepsThatSibling()
    {
        var result = Linker.Link("f63 20", Vocabulary(DatedCatalogue), NothingNamed);

        AssertAmbiguous(result, "F63", "F63-2013", "F63-2023", "F63-2026");
    }

    private static VocabularyView Vocabulary(params string[] values)
    {
        Dictionary<string, string> normalisedToOriginal = new(StringComparer.Ordinal);
        foreach (var value in values)
        {
            normalisedToOriginal[VocabularyFold.Fold(value)] = value;
        }

        return new VocabularyView { NormalisedToOriginal = normalisedToOriginal, Originals = values };
    }

    private static void AssertLinked(LinkResult result, string expected)
    {
        Assert.Equal(LinkOutcome.Linked, result.Outcome);
        Assert.Equal([expected], result.Candidates);
    }

    private static void AssertAmbiguous(LinkResult result, params string[] expectedSortedAscending)
    {
        Assert.Equal(LinkOutcome.Ambiguous, result.Outcome);
        Assert.Equal(expectedSortedAscending, result.Candidates);
    }
}

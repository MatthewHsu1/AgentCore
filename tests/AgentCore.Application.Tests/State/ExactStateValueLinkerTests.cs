using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// The §12 linker table: every row asserts a claim an earlier revision of the design got wrong in
/// prose. The vocabulary and mentions are the catalogue <c>linker_v9.py</c> (the Python reference
/// the C# port was cross-checked against) uses, so a failure here can be compared against that
/// script directly.
/// </summary>
/// <remarks>
/// Every non-ASCII literal here is a \uXXXX escape sequence, never a typed character — the same
/// rule <see cref="VocabularyFoldTests"/> follows, for the reason its header explains: a typed
/// decomposed character reaches disk pre-composed and the row would measure nothing.
/// </remarks>
public sealed class ExactStateValueLinkerTests
{
    private static readonly HashSet<string> NothingNamed = new(StringComparer.Ordinal);

    private static readonly string[] Catalogue =
    [
        "f63", "f65", "f80", "f85", "tt8",
        "ct800", "ct800ent", "ct850", "ct850ent",
        "xt185", "xt285", "xt285s", "xt385",
        "ct900", "ct900ent", "ctsbs900", "sole",
    ];

    private static readonly string[] Brands = ["sole", "truetread", "lifefitness"];

    // U+0E01 is the Thai consonant ko kai; U+0E48 and U+0E49 are the mai ek and mai tho tone
    // marks, both NonSpacingMark. The pair differs only by which mark it carries.
    private static readonly string[] ThaiToneMarkPair =
    [
        "\u0E01\u0E48900",
        "\u0E01\u0E49900",
    ];

    // U+0915 is the Devanagari consonant ka; U+093E and U+094B are the aa and o vowel signs,
    // both SpacingCombiningMark.
    private static readonly string[] DevanagariPair =
    [
        "\u0915\u093E900",
        "\u0915\u094B900",
    ];

    private static readonly ExactStateValueLinker Linker = new();

    [Fact]
    public void Link_JoinedTwoTokenRun_LinksWithEmptyLastNamed()
    {
        var result = Linker.Link("CT900 ENT", Vocabulary(Catalogue), NothingNamed);

        AssertLinked(result, "ct900ent");
    }

    [Fact]
    public void Link_ContainmentSwallowsTheShorterHit_LinksTheLongerValue()
    {
        var result = Linker.Link("a Sole F80", Vocabulary("f80", "solef80"), NothingNamed);

        AssertLinked(result, "solef80");
    }

    [Fact]
    public void Link_DisjointSpansOfOneValue_ContainmentDoesNotRequireTheLongerHitToStartTheSame()
    {
        var result = Linker.Link("CT 900 ENT", Vocabulary("ct900", "900ent", "ct900ent"), NothingNamed);

        AssertLinked(result, "ct900ent");
    }

    [Fact]
    public void Link_TwoDifferentIdsNamedInOneMention_IsAmbiguousNotASilentPick()
    {
        var result = Linker.Link("I have a CT900 and a CT900ENT", Vocabulary(Catalogue), NothingNamed);

        AssertAmbiguous(result, "ct900", "ct900ent");
    }

    [Fact]
    public void Link_BareIdAloneWithEmptyLastNamed_IsAmbiguousWithItsPrefixedSibling()
    {
        var result = Linker.Link("CT900", Vocabulary(Catalogue), NothingNamed);

        AssertAmbiguous(result, "ct900", "ct900ent");
    }

    [Fact]
    public void Link_BareIdAloneWithItselfInLastNamed_LinksIt()
    {
        var result = Linker.Link("CT900", Vocabulary(Catalogue), Named("ct900", "ct900ent", "ctsbs900"));

        AssertLinked(result, "ct900");
    }

    [Fact]
    public void Link_UnrelatedIdWithAForeignLastNamed_LinksWithoutNeedingTheTieBreak()
    {
        var result = Linker.Link("an XT385", Vocabulary(Catalogue), Named("ct900", "ct900ent", "ctsbs900"));

        AssertLinked(result, "xt385");
    }

    [Fact]
    public void Link_StrictPrefixHitWithEmptyLastNamed_IsAmbiguousNeverLinked()
    {
        var result = Linker.Link("a CT800", Vocabulary(Catalogue), NothingNamed);

        AssertAmbiguous(result, "ct800", "ct800ent");
    }

    [Fact]
    public void Link_TwoNonPrefixIdsNamedTogether_IsAmbiguousWithTheSurvivingHits()
    {
        var result = Linker.Link("a CT900 and an XT385", Vocabulary(Catalogue), NothingNamed);

        AssertAmbiguous(result, "ct900", "xt385");
    }

    [Fact]
    public void Link_NoRealIdSpelledOut_IsNoMatch()
    {
        var result = Linker.Link("so LE brand", Vocabulary(Catalogue), NothingNamed);

        AssertNoMatch(result);
    }

    [Fact]
    public void Link_CommaBreaksTheJoin_IsNoMatch()
    {
        var result = Linker.Link("my CT, 900 hours in", Vocabulary(Catalogue), NothingNamed);

        AssertNoMatch(result);
    }

    [Fact]
    public void Link_DotJoiner_Links()
    {
        var result = Linker.Link("CT. 900", Vocabulary(Catalogue), Named("ct900"));

        AssertLinked(result, "ct900");
    }

    [Fact]
    public void Link_HyphenJoiner_Links()
    {
        var result = Linker.Link("CT - 900", Vocabulary(Catalogue), Named("ct900"));

        AssertLinked(result, "ct900");
    }

    [Fact]
    public void Link_DoubleDotJoiner_Links()
    {
        var result = Linker.Link("C.T. 900", Vocabulary(Catalogue), Named("ct900"));

        AssertLinked(result, "ct900");
    }

    [Fact]
    public void Link_BareConsoleWord_DoesNotFuzzyMatchSoleInsideIt()
    {
        var result = Linker.Link("the console is broken", Vocabulary(Catalogue), NothingNamed);

        AssertNoMatch(result);
    }

    [Fact]
    public void Link_ConsoleSentenceAlsoNamingSole_LinksSole()
    {
        var result = Linker.Link("the console is broken on my Sole", Vocabulary(Catalogue), NothingNamed);

        AssertLinked(result, "sole");
    }

    [Fact]
    public void Link_DigitFreeTwoWordValue_JoinsOnTheThreeRuneRule()
    {
        var result = Linker.Link("a Life Fitness machine", Vocabulary(Brands), NothingNamed);

        AssertLinked(result, "lifefitness");
    }

    [Fact]
    public void Link_MixedCaseVocabularyWithTieBreak_LinksTheCollectionsOwnSpelling()
    {
        var result = Linker.Link("a CT900", Vocabulary("CT900", "CT900ENT"), Named("CT900"));

        AssertLinked(result, "CT900");
    }

    [Fact]
    public void Link_MixedCasePrefixWithNoTieBreak_CandidatesStayOrdinallySorted()
    {
        // "CT900ENT" sorts before "ct900" under StringComparer.Ordinal (uppercase before
        // lowercase), so a bare prepend of the exact hit ahead of a separately sorted prefix list
        // would leave this pair out of order. This locks in the fix.
        var result = Linker.Link("a ct900", Vocabulary("ct900", "CT900ENT"), NothingNamed);

        AssertAmbiguous(result, "CT900ENT", "ct900");
    }

    [Fact]
    public void Link_SpacedVocabularyValue_Links()
    {
        var result = Linker.Link("a CT 900", Vocabulary("CT 900"), NothingNamed);

        AssertLinked(result, "CT 900");
    }

    [Fact]
    public void Link_ThaiToneMarkMentionBare_LinksItsOwnId()
    {
        var result = Linker.Link(ThaiToneMarkPair[0], Vocabulary(ThaiToneMarkPair), NothingNamed);

        AssertLinked(result, ThaiToneMarkPair[0]);
    }

    [Fact]
    public void Link_ThaiToneMarkMentionInsideASentence_LinksItsOwnId()
    {
        var result = Linker.Link("a " + ThaiToneMarkPair[0] + " please", Vocabulary(ThaiToneMarkPair), NothingNamed);

        AssertLinked(result, ThaiToneMarkPair[0]);
    }

    [Fact]
    public void Link_ThaiToneMarkMentionSpacedBeforeTheDigits_LinksItsOwnId()
    {
        var result = Linker.Link("\u0E01\u0E48 900", Vocabulary(ThaiToneMarkPair), NothingNamed);

        AssertLinked(result, ThaiToneMarkPair[0]);
    }

    [Fact]
    public void Link_TheOtherThaiToneMarkMention_DoesNotCrossLinkToItsSibling()
    {
        var result = Linker.Link(ThaiToneMarkPair[1], Vocabulary(ThaiToneMarkPair), NothingNamed);

        AssertLinked(result, ThaiToneMarkPair[1]);
    }

    [Fact]
    public void Link_DevanagariMention_LinksItsOwnId()
    {
        var result = Linker.Link("my " + DevanagariPair[0], Vocabulary(DevanagariPair), NothingNamed);

        AssertLinked(result, DevanagariPair[0]);
    }

    [Fact]
    public void BuildCandidates_SixtyTokenMention_StaysWithinTheFourTimesTokenCountBound()
    {
        var mention = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"word{i}"));
        var (tokens, separators) = VocabularyFold.Tokenize(mention);

        var candidates = ExactStateValueLinker.BuildCandidates(tokens, separators);

        Assert.Equal(60, tokens.Count);
        Assert.True(candidates.Count <= 4 * 60, $"expected at most {4 * 60} candidates, got {candidates.Count}");
        Assert.Equal(234, candidates.Count);
    }

    [Theory]
    [InlineData(LinkOutcome.Linked, 0)]
    [InlineData(LinkOutcome.Linked, 2)]
    [InlineData(LinkOutcome.NoMatch, 1)]
    [InlineData(LinkOutcome.Ambiguous, 1)]
    public void LinkResult_ACandidateCountThatContradictsTheOutcome_IsRefused(LinkOutcome outcome, int candidates)
    {
        // IStateValueLinker is public, so a host linker is arbitrary code. The extractor indexes
        // Candidates[0] on a Linked verdict rather than re-testing what this type already promises.
        Assert.Throws<ArgumentException>(
            () => new LinkResult(outcome, [.. Enumerable.Range(0, candidates).Select(i => $"v{i}")]));
    }

    private static HashSet<string> Named(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

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

    private static void AssertNoMatch(LinkResult result)
    {
        Assert.Equal(LinkOutcome.NoMatch, result.Outcome);
        Assert.Empty(result.Candidates);
    }
}

/// <summary>
/// <see cref="StateValueLinkers"/>: the name-keyed registry K12 asks for, always seeded with
/// <c>exact</c>.
/// </summary>
public sealed class StateValueLinkersTests
{
    [Fact]
    public void Resolve_Exact_ReturnsTheBuiltInLinker()
    {
        var registry = new StateValueLinkers([]);

        var linker = registry.Resolve("exact");

        Assert.IsType<ExactStateValueLinker>(linker);
    }

    [Fact]
    public void Names_AlwaysIncludesExactEvenWithNoHostLinkers()
    {
        var registry = new StateValueLinkers([]);

        Assert.Contains("exact", registry.Names);
    }

    [Fact]
    public void Resolve_AnUnregisteredName_Throws()
    {
        var registry = new StateValueLinkers([]);

        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("bogus"));
    }

    [Fact]
    public void Constructor_TwoHostLinkersShareAName_Throws()
        => Assert.Throws<ArgumentException>(() => new StateValueLinkers(
        [
            new NamedLinker("custom"),
            new NamedLinker("custom"),
        ]));

    private sealed class NamedLinker(string name) : IStateValueLinker
    {
        public string Name { get; } = name;

        public LinkResult Link(string mention, VocabularyView vocabulary, IReadOnlySet<string> lastNamed)
            => throw new NotSupportedException();
    }
}

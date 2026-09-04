using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// <see cref="VocabularyFold"/>: the K31 fold, and the tokeniser that shares its predicate.
/// </summary>
/// <remarks>
/// Every non-ASCII literal here is a \uXXXX / \UXXXXXXXX escape sequence, never a typed
/// character. Round 12 of the design's review wrote a decomposed probe input as typed characters,
/// which an editor or a file-writing tool had already re-composed on disk, so the check silently
/// measured nothing. This file is written the same way for every row.
/// <para>
/// Rows that hold only where the runtime composes Unicode live in
/// AgentCore.Application.Unicode.Tests instead. This project runs under the repo-wide
/// InvariantGlobalization=true that production ships, where NFC composition is a no-op.
/// </para>
/// </remarks>
public sealed class VocabularyFoldTests
{
    [Fact]
    public void Fold_LetterWithRingAboveVsPlainLetter_DoesNotCollide()
    {
        var withRing = VocabularyFold.Fold("\u00C5T900");
        var plain = VocabularyFold.Fold("T900");

        Assert.NotEqual(withRing, plain);
    }

    [Fact]
    public void Fold_SharpSVsDoubleS_DoesNotCollide()
    {
        var sharpS = VocabularyFold.Fold("\u00DFf80");
        var doubleS = VocabularyFold.Fold("SSF80");

        Assert.NotEqual(sharpS, doubleS);
    }

    [Fact]
    public void Fold_ThaiToneMarkPresentVsAbsent_DoesNotCollide()
    {
        // \u0E01 is the Thai consonant ko kai; \u0E48 is the mai ek tone mark, a
        // NonSpacingMark. K31 keeps marks rather than stripping them.
        var withToneMark = VocabularyFold.Fold("\u0E01\u0E48");
        var withoutToneMark = VocabularyFold.Fold("\u0E01");

        Assert.NotEqual(withToneMark, withoutToneMark);
    }

    [Fact]
    public void Fold_TwoAstralLetters_StayDistinct()
    {
        // \U0001D400 and \U0001D401 are astral (supplementary-plane) mathematical capital letters.
        // char-based iteration would see two lone surrogate halves per id, both classified
        // UnicodeCategory.Surrogate and so dropped by Keep, collapsing both ids to "id" and
        // colliding them. Rune-based iteration reads one codepoint, correctly classified Lu.
        var first = VocabularyFold.Fold("id\U0001D400");
        var second = VocabularyFold.Fold("id\U0001D401");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Fold_PunctuationOnlyValue_IsEmpty()
    {
        Assert.Equal(string.Empty, VocabularyFold.Fold("***"));
    }

    [Fact]
    public void Fold_MixedCaseSpacedHyphenatedValue_DropsSeparatorsAndLowersCase()
    {
        Assert.Equal("north900pro", VocabularyFold.Fold("North-900 Pro"));
    }

    [Fact]
    public void Tokenize_SpacedHyphenatedMention_DropsLeadingAndTrailingSeparators()
    {
        var (tokens, separators) = VocabularyFold.Tokenize("  north-900 pro  ");

        Assert.Equal(["north", "900", "pro"], tokens);
        Assert.Equal(["-", " "], separators);
    }

    [Fact]
    public void Tokenize_TokensConcatenated_EqualFoldOfTheWholeMention()
    {
        const string Mention = "  North-900 \u00C5Pro!! ";

        var (tokens, _) = VocabularyFold.Tokenize(Mention);

        Assert.Equal(VocabularyFold.Fold(Mention), string.Concat(tokens));
    }

    [Fact]
    public void Tokenize_NoKeptRunes_ReturnsNoTokensAndNoSeparators()
    {
        var (tokens, separators) = VocabularyFold.Tokenize("!!! ---");

        Assert.Empty(tokens);
        Assert.Empty(separators);
    }

    [Fact]
    public void Tokenize_OneTokenNoSeparators_ReturnsOneTokenAndNoSeparators()
    {
        var (tokens, separators) = VocabularyFold.Tokenize("T900");

        Assert.Equal(["t900"], tokens);
        Assert.Empty(separators);
    }
}

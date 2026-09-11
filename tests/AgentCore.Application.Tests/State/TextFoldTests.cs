using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// <see cref="TextFold"/>: NFC, lower-invariant, letters/numbers/marks only.
/// </summary>
/// <remarks>
/// Every non-ASCII literal here is a \uXXXX / \UXXXXXXXX escape sequence, never a typed
/// character. A decomposed input written as typed characters is re-composed on disk by an editor
/// or a file-writing tool, so the check would silently measure nothing.
/// <para>
/// Rows that hold only where the runtime composes Unicode live in
/// AgentCore.Application.Unicode.Tests instead. This project runs under the repo-wide
/// InvariantGlobalization=true that production ships, where NFC composition is a no-op.
/// </para>
/// </remarks>
public sealed class TextFoldTests
{
    [Fact]
    public void Fold_LetterWithRingAboveVsPlainLetter_DoesNotCollide()
    {
        var withRing = TextFold.Fold("\u00C5T900");
        var plain = TextFold.Fold("T900");

        Assert.NotEqual(withRing, plain);
    }

    [Fact]
    public void Fold_SharpSVsDoubleS_DoesNotCollide()
    {
        var sharpS = TextFold.Fold("\u00DFf80");
        var doubleS = TextFold.Fold("SSF80");

        Assert.NotEqual(sharpS, doubleS);
    }

    [Fact]
    public void Fold_ThaiToneMarkPresentVsAbsent_DoesNotCollide()
    {
        // \u0E01 is the Thai consonant ko kai; \u0E48 is the mai ek tone mark, a
        // NonSpacingMark. K31 keeps marks rather than stripping them.
        var withToneMark = TextFold.Fold("\u0E01\u0E48");
        var withoutToneMark = TextFold.Fold("\u0E01");

        Assert.NotEqual(withToneMark, withoutToneMark);
    }

    [Fact]
    public void Fold_TwoAstralLetters_StayDistinct()
    {
        // \U0001D400 and \U0001D401 are astral (supplementary-plane) mathematical capital letters.
        // char-based iteration would see two lone surrogate halves per id, both classified
        // UnicodeCategory.Surrogate and so dropped by Keep, collapsing both ids to "id" and
        // colliding them. Rune-based iteration reads one codepoint, correctly classified Lu.
        var first = TextFold.Fold("id\U0001D400");
        var second = TextFold.Fold("id\U0001D401");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Fold_PunctuationOnlyValue_IsEmpty()
    {
        Assert.Equal(string.Empty, TextFold.Fold("***"));
    }

    [Fact]
    public void Fold_MixedCaseSpacedHyphenatedValue_DropsSeparatorsAndLowersCase()
    {
        Assert.Equal("north900pro", TextFold.Fold("North-900 Pro"));
    }
}

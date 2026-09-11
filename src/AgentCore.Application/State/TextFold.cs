using System.Globalization;
using System.Text;

namespace AgentCore.Application.State;

/// <summary>
/// Folds text to a comparable form: NFC, invariant lower case, then keep only letters, numbers
/// and marks. Both sides of a comparison go through the same fold, so neither can see a class
/// the other was not also normalised by.
/// </summary>
/// <remarks>
/// <see cref="Rune"/> throughout, never <see cref="char"/>: an astral codepoint is a surrogate
/// pair, and classifying each half separately reads both as <see cref="UnicodeCategory.Surrogate"/>
/// — not a kept category — which would silently drop the codepoint instead of keeping it whole.
/// </remarks>
internal static class TextFold
{
    /// <summary>Normalises one string to its comparable form.</summary>
    /// <param name="value">The original string.</param>
    /// <returns>NFC, lower-invariant, letters/numbers/marks only. Empty when nothing survives.</returns>
    internal static string Fold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var composed = value.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        StringBuilder folded = new(composed.Length);
        Span<char> utf16 = stackalloc char[2];

        foreach (var rune in composed.EnumerateRunes())
        {
            if (Keep(rune))
            {
                folded.Append(utf16[..rune.EncodeToUtf16(utf16)]);
            }
        }

        return folded.ToString();
    }

    private static bool Keep(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or
        UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or
        UnicodeCategory.OtherLetter or
        UnicodeCategory.DecimalDigitNumber or
        UnicodeCategory.LetterNumber or
        UnicodeCategory.OtherNumber or
        UnicodeCategory.NonSpacingMark or
        UnicodeCategory.SpacingCombiningMark;
}

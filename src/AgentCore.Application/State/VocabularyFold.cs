using System.Globalization;
using System.Text;

namespace AgentCore.Application.State;

/// <summary>
/// The K31 fold: NFC, invariant lower case, then keep only letters, numbers and marks. One
/// predicate drives both the vocabulary's fold and the mention's tokeniser, so a caller can never
/// see a class the vocabulary was not also normalised by.
/// </summary>
/// <remarks>
/// <see cref="Rune"/> throughout, never <see cref="char"/>: an astral codepoint is a surrogate
/// pair, and classifying each half separately reads both as <see cref="UnicodeCategory.Surrogate"/>
/// — not a kept category — which would silently drop the codepoint instead of keeping it whole.
/// </remarks>
internal static class VocabularyFold
{
    /// <summary>Normalises one vocabulary value or mention span to its comparable form.</summary>
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

    /// <summary>Splits a mention into the runs <see cref="Fold"/> would keep, and the runs between them it would drop.</summary>
    /// <param name="mention">The extractor's raw mention text.</param>
    /// <returns>
    /// <c>Tokens</c>: the maximal runs of kept runes, each already folded, in order.
    /// <c>Separators</c>: the runs of dropped runes strictly between two tokens — one fewer than
    /// the token count. A leading or trailing run of dropped runes bounds nothing, so it is not
    /// reported.
    /// </returns>
    internal static (IReadOnlyList<string> Tokens, IReadOnlyList<string> Separators) Tokenize(string mention)
    {
        ArgumentNullException.ThrowIfNull(mention);

        var composed = mention.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        List<string> tokens = [];
        List<string> separators = [];
        StringBuilder buffer = new();
        Span<char> utf16 = stackalloc char[2];
        var inToken = false;
        var sawToken = false;

        foreach (var rune in composed.EnumerateRunes())
        {
            var kept = Keep(rune);
            if (kept != inToken)
            {
                if (inToken)
                {
                    tokens.Add(buffer.ToString());
                    sawToken = true;
                }
                else if (sawToken)
                {
                    // A leading run of dropped runes bounds no token on its left, so only a run
                    // that follows one is a separator.
                    separators.Add(buffer.ToString());
                }

                buffer.Clear();
                inToken = kept;
            }

            buffer.Append(utf16[..rune.EncodeToUtf16(utf16)]);
        }

        if (inToken)
        {
            tokens.Add(buffer.ToString());
        }

        return (tokens, separators);
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

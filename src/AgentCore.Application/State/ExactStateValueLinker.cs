using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace AgentCore.Application.State;

/// <summary>
/// The <c>exact</c> linker (K9): fold, bounded join, exact match, span resolution. There is no
/// trigram or embedding fallback — a near-tie leaves the slot unfilled and names the candidates
/// (K11) rather than guessing.
/// </summary>
/// <remarks>
/// Tokenising is <see cref="VocabularyFold.Tokenize"/> rather than a second copy of K31's
/// predicate; the vocabulary's fold-to-original map is built once at boot by
/// <see cref="VocabularyCache.Replace"/>, so this type never folds a vocabulary value or checks it
/// for a collision itself.
/// </remarks>
public sealed class ExactStateValueLinker : IStateValueLinker
{
    /// <summary>The longest run of tokens one candidate spelling may join.</summary>
    private const int MaxRun = 4;

    /// <summary>
    /// Separator runes that always break a join, regardless of whitespace. An enumerated list, not
    /// a Unicode class: <c>Po</c> contains <c>.</c> and <c>/</c>, which §6 needs to join.
    /// </summary>
    private static readonly HashSet<Rune> PhraseBreaks = [.. ",;:!?".EnumerateRunes()];

    /// <summary>
    /// One ordinally sorted copy of each view's normalised keys, memoised for as long as the view
    /// itself lives. A view is immutable and a refresh swaps the reference (section 5), so an entry
    /// can never describe a vocabulary that has since changed.
    /// </summary>
    private static readonly ConditionalWeakTable<VocabularyView, string[]> KeysInOrder = new();

    /// <inheritdoc />
    public string Name => "exact";

    /// <inheritdoc />
    public LinkResult Link(string mention, VocabularyView vocabulary, IReadOnlySet<string> lastNamed)
    {
        ArgumentNullException.ThrowIfNull(mention);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(lastNamed);

        var (tokens, separators) = VocabularyFold.Tokenize(mention);
        var candidates = BuildCandidates(tokens, separators);
        var table = vocabulary.NormalisedToOriginal;

        // Driven by the candidates, not by the vocabulary: a mention yields at most four spellings
        // per token, where the vocabulary may hold thousands of values. The intersection is the same
        // either way round.
        Dictionary<string, HashSet<(int Start, int End)>> hits = new(StringComparer.Ordinal);
        foreach (var (key, spans) in candidates)
        {
            if (table.ContainsKey(key))
            {
                hits[key] = spans;
            }
        }

        var kept = Resolve(hits);

        if (kept.Count == 0)
        {
            return new LinkResult(LinkOutcome.NoMatch, []);
        }

        if (kept.Count > 1)
        {
            return new LinkResult(
                LinkOutcome.Ambiguous,
                kept.Select(key => table[key]).Order(StringComparer.Ordinal).ToList());
        }

        var only = kept[0];
        var original = table[only];

        if (lastNamed.Contains(original))
        {
            return new LinkResult(LinkOutcome.Linked, [original]);
        }

        var prefixedBy = PrefixedSiblings(vocabulary, only);

        if (prefixedBy.Count == 0)
        {
            return new LinkResult(LinkOutcome.Linked, [original]);
        }

        // K21's tie-break did not apply, and `only` is a strict prefix of another value in the
        // collection: the caller could mean either, so this stays a near-tie rather than a guess.
        // The full set is re-sorted rather than just prepending `original` ahead of a sorted
        // `prefixedBy`: a mixed-case vocabulary can make `original` compare greater than one of its
        // own prefixed siblings under ordinal order (uppercase sorts before lowercase), which a bare
        // prepend would leave out of order.
        prefixedBy.Add(original);
        prefixedBy.Sort(StringComparer.Ordinal);
        return new LinkResult(LinkOutcome.Ambiguous, prefixedBy);
    }

    /// <summary>
    /// Every value whose normalised key has <paramref name="key"/> as a strict prefix, in the
    /// collection's own spelling.
    /// </summary>
    /// <remarks>
    /// Under ordinal order every strict prefix extension of <paramref name="key"/> sorts after it
    /// and the extensions are contiguous, so this is a binary search plus a walk over the matches
    /// rather than a scan of the whole vocabulary.
    /// </remarks>
    private static List<string> PrefixedSiblings(VocabularyView vocabulary, string key)
    {
        var ordered = KeysInOrder.GetValue(
            vocabulary,
            view => [.. view.NormalisedToOriginal.Keys.Order(StringComparer.Ordinal)]);

        var found = Array.BinarySearch(ordered, key, StringComparer.Ordinal);
        var scan = found < 0 ? ~found : found + 1;

        List<string> siblings = [];
        for (; scan < ordered.Length && ordered[scan].StartsWith(key, StringComparison.Ordinal); scan++)
        {
            siblings.Add(vocabulary.NormalisedToOriginal[ordered[scan]]);
        }

        return siblings;
    }

    /// <summary>
    /// Discards a hit whose every span sits inside a span of some longer hit (K10) — tested per
    /// span, so different spans of one hit may be swallowed by different longer hits, and no longer
    /// hit need begin with the shorter one.
    /// </summary>
    private static List<string> Resolve(Dictionary<string, HashSet<(int Start, int End)>> hits)
    {
        List<string> kept = [];

        foreach (var (hit, spans) in hits)
        {
            var longer = hits.Keys.Where(other => other.Length > hit.Length).ToList();
            var swallowed = longer.Count > 0
                && spans.All(span => longer.Any(other => hits[other].Any(outer => Contains(outer, span))));

            if (!swallowed)
            {
                kept.Add(hit);
            }
        }

        return kept;
    }

    private static bool Contains((int Start, int End) outer, (int Start, int End) inner)
        => outer.Start <= inner.Start && inner.End <= outer.End;

    /// <summary>
    /// Builds every candidate spelling §6 step 2 admits: every single token, and every contiguous
    /// run of up to <see cref="MaxRun"/> tokens whose separators are all joinable. Each run is tested
    /// on its own — a run that fails does not stop a longer run starting at the same token.
    /// </summary>
    /// <param name="tokens">The mention's tokens, in order.</param>
    /// <param name="separators">The runs between adjacent tokens — one fewer entry than <paramref name="tokens"/>.</param>
    /// <returns>The map from candidate spelling to every token span that produced it.</returns>
    internal static Dictionary<string, HashSet<(int Start, int End)>> BuildCandidates(
        IReadOnlyList<string> tokens, IReadOnlyList<string> separators)
    {
        Dictionary<string, HashSet<(int Start, int End)>> candidates = new(StringComparer.Ordinal);

        for (var start = 0; start < tokens.Count; start++)
        {
            var limit = Math.Min(start + MaxRun, tokens.Count);

            for (var end = start; end < limit; end++)
            {
                var run = new List<string>(end - start + 1);
                for (var index = start; index <= end; index++)
                {
                    run.Add(tokens[index]);
                }

                // Each run is tested on its own, against its own full token list — never by
                // extending a shorter run's verdict. A separator's joinability can depend on a
                // token elsewhere in the run (the digit rule), so a run of 3 can join where its
                // own 2-token prefix would not.
                if (end > start && !IsJoinable(separators, start, end, run))
                {
                    continue;
                }

                var key = string.Concat(run);
                if (!candidates.TryGetValue(key, out var spans))
                {
                    candidates[key] = spans = [];
                }

                spans.Add((start, end));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Whether every separator inside <c>[start, end)</c> joins the whole run, by §6 step 2's three
    /// rules in order: a phrase-breaking rune never joins; otherwise a separator with no whitespace
    /// always joins; otherwise the run joins only when
    /// <see cref="RunJoinsAcrossWhitespace(IReadOnlyList{string})"/> allows it.
    /// </summary>
    private static bool IsJoinable(IReadOnlyList<string> separators, int start, int end, IReadOnlyList<string> run)
    {
        // The third rule reads the run and not the separator, so its verdict is the same for every
        // separator here: taken at most once, and only once a separator actually needs it.
        bool? runJoins = null;

        for (var index = start; index < end; index++)
        {
            var hasWhitespace = false;

            foreach (var rune in separators[index].EnumerateRunes())
            {
                if (PhraseBreaks.Contains(rune) || Rune.IsControl(rune))
                {
                    return false;
                }

                hasWhitespace = hasWhitespace || Rune.IsWhiteSpace(rune);
            }

            if (!hasWhitespace)
            {
                continue;
            }

            runJoins ??= RunJoinsAcrossWhitespace(run);
            if (!runJoins.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// §6 step 2's third rule: a whitespace separator joins the run only when the run carries a
    /// digit, or every token in it is at least three runes long.
    /// </summary>
    private static bool RunJoinsAcrossWhitespace(IReadOnlyList<string> run)
    {
        var allLongEnough = true;

        foreach (var token in run)
        {
            var runes = 0;

            foreach (var rune in token.EnumerateRunes())
            {
                if (IsNumber(rune))
                {
                    return true;
                }

                runes++;
            }

            allLongEnough = allLongEnough && runes >= 3;
        }

        return allLongEnough;
    }

    private static bool IsNumber(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
}

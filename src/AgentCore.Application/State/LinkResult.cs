namespace AgentCore.Application.State;

/// <summary>The three outcomes an <see cref="IStateValueLinker"/> can return (§6 step 4).</summary>
public enum LinkOutcome
{
    /// <summary>Nothing in the vocabulary matched. The slot stays unfilled.</summary>
    NoMatch,

    /// <summary>Exactly one value matched, with no unresolved tie.</summary>
    Linked,

    /// <summary>More than one value is a plausible match, and none can be preferred (K11).</summary>
    Ambiguous,
}

/// <summary>One linker verdict.</summary>
/// <param name="Outcome">Which of the three outcomes this is.</param>
/// <param name="Candidates">
/// The matching values, in the collection's own spelling, sorted with
/// <see cref="StringComparer.Ordinal"/>.
/// </param>
/// <exception cref="ArgumentException">
/// The candidate count does not match the outcome. <see cref="IStateValueLinker"/> is public, so a
/// host linker is arbitrary code; checking here makes the count an invariant the extractor can
/// index on rather than a promise it has to re-test.
/// </exception>
public sealed record LinkResult(LinkOutcome Outcome, IReadOnlyList<string> Candidates)
{
    /// <summary>
    /// Gets the matching values, in the collection's own spelling, sorted with
    /// <see cref="StringComparer.Ordinal"/>. <see cref="LinkOutcome.NoMatch"/> carries none,
    /// <see cref="LinkOutcome.Linked"/> exactly one, and <see cref="LinkOutcome.Ambiguous"/> at
    /// least two.
    /// </summary>
    public IReadOnlyList<string> Candidates { get; init; } = Checked(Outcome, Candidates);

    private static IReadOnlyList<string> Checked(LinkOutcome outcome, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var holds = outcome switch
        {
            LinkOutcome.NoMatch => candidates.Count == 0,
            LinkOutcome.Linked => candidates.Count == 1,
            LinkOutcome.Ambiguous => candidates.Count > 1,
            _ => false,
        };

        return holds
            ? candidates
            : throw new ArgumentException(
                $"a {outcome} verdict cannot carry {candidates.Count} candidates.",
                nameof(candidates));
    }
}

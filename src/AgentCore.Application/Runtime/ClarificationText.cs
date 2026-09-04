namespace AgentCore.Application.Runtime;

/// <summary>
/// The sentences §7 specifies for both ambiguity channels. One rule, two wordings: channel 1 rides a
/// standing turn instruction and must never claim to suppress a card the search already returned;
/// channel 2 rides an empty search result and may say so plainly.
/// </summary>
internal static class ClarificationText
{
    /// <summary>Reads the wording a slot is described by, for either channel's sentence.</summary>
    /// <param name="slot">The slot's name.</param>
    /// <param name="descriptions">Each slot's configured <c>description</c>, by name.</param>
    /// <returns>
    /// The configured description, or the slot's own name when the document describes it with
    /// nothing else. Both channels resolve through here, so the two sentences never disagree about
    /// what the caller is being asked about.
    /// </returns>
    internal static string DescriptionOf(string slot, IReadOnlyDictionary<string, string?> descriptions)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(descriptions);

        return descriptions.TryGetValue(slot, out var configured) && !string.IsNullOrEmpty(configured)
            ? configured
            : slot;
    }

    /// <summary>Renders channel 1's sentence: the clarification, as a turn instruction (K29).</summary>
    /// <param name="description">
    /// The slot's <c>description</c>, or the slot name when the caller has none — used as an
    /// apposition, because a deployer's description is a whole sentence.
    /// </param>
    /// <param name="candidates">The slot's pending candidate set.</param>
    /// <param name="maxCandidates">Above this many candidates, the list is omitted.</param>
    /// <param name="first">
    /// Whether this is the first such message the turn speaks. A second opens "Another thing…" rather
    /// than repeating "One thing…", so two pending slots do not read as the same question twice.
    /// </param>
    /// <returns>The sentence.</returns>
    internal static string Instruction(
        string description, IReadOnlyList<string> candidates, int maxCandidates, bool first)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 1)
        {
            var confirm = first ? "One thing is not yet confirmed" : "Another thing is not yet confirmed";
            return $"{confirm}: {description} Everything found is for {candidates[0]}. "
                + "Ask the caller whether that is what they have before giving advice specific to it.";
        }

        var known = first ? "One thing is not yet known" : "Another thing is not yet known";

        if (candidates.Count > maxCandidates)
        {
            return $"{known}: {description} Ask the caller, and do not give advice specific to one until "
                + "they answer.";
        }

        return $"{known}: {description} It is one of {JoinAlternatives(candidates)}. Ask the caller, and do "
            + "not give advice that is specific to one until they answer. Anything that applies to all of "
            + "them is still fair game.";
    }

    /// <summary>Renders channel 2's sentence: the probe's note, as a search result.</summary>
    /// <param name="description">
    /// The slot's <c>description</c>, or the slot name when the caller has none — an apposition, for
    /// the same reason <see cref="Instruction"/>'s does.
    /// </param>
    /// <param name="candidates">The slot's pending candidate set.</param>
    /// <param name="maxCandidates">Above this many candidates, the list is omitted.</param>
    /// <returns>The sentence.</returns>
    internal static string Note(string description, IReadOnlyList<string> candidates, int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 1)
        {
            return "One thing decides the answer here and is not yet confirmed: "
                + $"{description} Everything found is for {candidates[0]}. Ask the caller whether that is "
                + "what they have before answering from the knowledge base about it.";
        }

        const string decides = "One thing decides the answer here and is not yet known";

        if (candidates.Count > maxCandidates)
        {
            // "which" needs the list as its antecedent, the same way channel 1's over-cap form drops
            // "Anything that applies to all of them" rather than leave "them" pointing at a list that
            // is no longer there.
            return $"{decides}: {description} Ask the caller, and do not answer from the knowledge base "
                + "about it until they say.";
        }

        return $"{decides}: {description} It could be: {string.Join(", ", candidates)}. Ask the caller "
            + "which, and do not answer from the knowledge base about it until they say.";
    }

    /// <summary>
    /// Joins candidates as alternatives — "X or Y", or "X, Y, or Z" — the phrasing channel 1's "It is
    /// one of" reads as. Channel 2's "It could be:" is a plain list instead (see <see cref="Note"/>),
    /// which is why the two channels do not share this helper.
    /// </summary>
    private static string JoinAlternatives(IReadOnlyList<string> candidates)
        => candidates.Count == 2
            ? $"{candidates[0]} or {candidates[1]}"
            : string.Join(", ", candidates.Take(candidates.Count - 1)) + ", or " + candidates[^1];
}

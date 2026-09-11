namespace AgentCore.Application.Runtime;

/// <summary>
/// The sentence §8 specifies for the knowledge probe's note. It rides an empty search result, so it
/// may say plainly that the answer is not yet known.
/// </summary>
internal static class ClarificationText
{
    /// <summary>Reads the wording a slot is described by.</summary>
    /// <param name="slot">The slot's name.</param>
    /// <param name="descriptions">Each slot's configured <c>description</c>, by name.</param>
    /// <returns>
    /// The configured description, or the slot's own name when the document describes it with
    /// nothing else.
    /// </returns>
    internal static string DescriptionOf(string slot, IReadOnlyDictionary<string, string?> descriptions)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(descriptions);

        return descriptions.TryGetValue(slot, out var configured) && !string.IsNullOrEmpty(configured)
            ? configured
            : slot;
    }

    /// <summary>Renders the probe's note, as a search result.</summary>
    /// <param name="description">
    /// The slot's <c>description</c>, or the slot name when the caller has none — used as an
    /// apposition, because a deployer's description is a whole sentence.
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
            // "which" needs the list as its antecedent, so the over-cap form drops it.
            return $"{decides}: {description} Ask the caller, and do not answer from the knowledge base "
                + "about it until they say.";
        }

        return $"{decides}: {description} It could be: {string.Join(", ", candidates)}. Ask the caller "
            + "which, and do not answer from the knowledge base about it until they say.";
    }
}

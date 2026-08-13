namespace AgentCore.Domain.Knowledge;

/// <summary>
/// The document ids one list call names.
/// </summary>
/// <remarks>
/// A store caps how many ids it names, because a large tree otherwise fills the model context with a
/// directory. <see cref="Truncated"/> says the cap cut the answer, so the model narrows the pattern
/// rather than reads a short list as the whole tree.
/// </remarks>
public sealed record DocumentListing
{
    /// <summary>Gets the ids, in ordinal order, so two calls answer the same.</summary>
    public required IReadOnlyList<string> DocumentIds { get; init; }

    /// <summary>Gets whether the cap cut the answer.</summary>
    public required bool Truncated { get; init; }
}

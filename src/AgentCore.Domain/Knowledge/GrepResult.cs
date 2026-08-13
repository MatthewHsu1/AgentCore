namespace AgentCore.Domain.Knowledge;

/// <summary>
/// The lines one grep call matched.
/// </summary>
/// <remarks>
/// A store caps how many lines it returns, because a wide pattern otherwise fills the model context.
/// <see cref="Truncated"/> says the cap cut the answer, so the model narrows the pattern rather than
/// reads the first matches as every match.
/// </remarks>
public sealed record GrepResult
{
    /// <summary>Gets the matches, in ordinal order of document id and then by line, so two calls answer the same.</summary>
    public required IReadOnlyList<GrepMatch> Matches { get; init; }

    /// <summary>Gets whether the cap cut the answer.</summary>
    public required bool Truncated { get; init; }
}

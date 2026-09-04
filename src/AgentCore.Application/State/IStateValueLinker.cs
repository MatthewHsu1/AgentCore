namespace AgentCore.Application.State;

/// <summary>
/// Turns a caller's loose mention of a <c>vocabulary:</c> slot's value into the collection's own
/// spelling, or reports that it cannot be resolved without asking (K11, K12).
/// </summary>
public interface IStateValueLinker
{
    /// <summary>Gets the name a slot's <c>vocabulary.linker</c> resolves this linker by.</summary>
    string Name { get; }

    /// <summary>Links one extractor mention against one slot's vocabulary.</summary>
    /// <param name="mention">The extractor's raw mention text.</param>
    /// <param name="vocabulary">The slot's vocabulary, sampled once at call open (K40).</param>
    /// <param name="lastNamed">
    /// The candidate set actually spoken to the caller for this slot in this call — K37's record,
    /// in the collection's own spelling — empty when nothing has been. Never a pending list a
    /// channel installed but stayed silent about. A set, not a list: both ambiguity channels record
    /// one under <see cref="StringComparer.Ordinal"/>, and a linker only ever asks it for membership.
    /// </param>
    /// <returns>The verdict.</returns>
    LinkResult Link(string mention, VocabularyView vocabulary, IReadOnlySet<string> lastNamed);
}

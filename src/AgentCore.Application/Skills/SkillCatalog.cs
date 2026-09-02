using Microsoft.Agents.AI;

namespace AgentCore.Application.Skills;

/// <summary>
/// The skills a host bound, and the names they serve.
/// </summary>
public sealed class SkillCatalog
{
    /// <summary>Creates the catalog.</summary>
    /// <param name="source">The shared source every agent's filter wraps.</param>
    /// <param name="names">Every skill name the source serves.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SkillCatalog(AgentSkillsSource source, IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(names);

        Source = source;
        Names = names;
    }

    /// <summary>Gets the shared source. One per process, already cached.</summary>
    public AgentSkillsSource Source { get; }

    /// <summary>Gets every skill name the source serves.</summary>
    public IReadOnlySet<string> Names { get; }
}

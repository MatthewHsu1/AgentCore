using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Runtime;

/// <summary>Opens the knowledge scope of one turn over this flow of execution.</summary>
public static class KnowledgeScopeScope
{
    /// <summary>Gets the scope open on this flow, or <see langword="null"/> when none is.</summary>
    public static KnowledgeScope? Current => TurnAmbients.Current?.Knowledge;

    /// <summary>Opens one scope over this flow.</summary>
    /// <param name="scope">What the turn may see.</param>
    /// <returns>The scope. Disposing it puts back what was ambient before.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public static IDisposable Open(KnowledgeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return TurnAmbients.Amend(ambients => ambients with { Knowledge = scope });
    }
}

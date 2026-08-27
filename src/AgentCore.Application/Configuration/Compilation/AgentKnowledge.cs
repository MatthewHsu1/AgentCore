using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// One agent's <c>knowledge:</c> block, fully resolved: every field inherited or defaulted, so
/// nothing downstream has to ask whether a value came from the agent, the defaults, or nowhere.
/// </summary>
/// <param name="Mode">How this agent reaches the knowledge base.</param>
/// <param name="Limit">How many cards to retrieve.</param>
/// <param name="Citations">Whether the model sees the source label.</param>
/// <param name="Scoped">Whether this agent's searches are confined to a scope.</param>
public sealed record ResolvedKnowledge(KnowledgeMode Mode, int Limit, bool Citations, bool Scoped);

/// <summary>
/// Composes one agent's <c>knowledge:</c> block from <c>agents.defaults.knowledge</c> and the
/// agent's own block, key by key.
/// </summary>
public static class AgentKnowledge
{
    /// <summary>The mode used when neither the agent nor the defaults name one.</summary>
    public const KnowledgeMode DefaultMode = KnowledgeMode.Prefetch;

    /// <summary>The limit used when neither the agent nor the defaults name one.</summary>
    public const int DefaultLimit = 5;

    /// <summary>
    /// The citations flag used when neither the agent nor the defaults name one.
    /// </summary>
    public const bool DefaultCitations = false;

    /// <summary>
    /// The scoped flag used when neither the agent nor the defaults name one.
    /// </summary>
    public const bool DefaultScoped = true;

    /// <summary>Composes one agent's resolved knowledge settings.</summary>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="agent">The agent to resolve.</param>
    /// <returns>
    /// The resolved settings, or <see langword="null"/> when neither the defaults nor the agent
    /// declares a <c>knowledge:</c> block — that agent has nothing to do with the knowledge base,
    /// and pays nothing for it.
    /// </returns>
    public static ResolvedKnowledge? Compose(AgentDefaults? defaults, AgentConfiguration agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var own = agent.Knowledge;
        var shared = defaults?.Knowledge;

        if (own is null && shared is null)
        {
            return null;
        }

        return new ResolvedKnowledge(
            own?.Mode ?? shared?.Mode ?? DefaultMode,
            own?.Limit ?? shared?.Limit ?? DefaultLimit,
            own?.Citations ?? shared?.Citations ?? DefaultCitations,
            own?.Scoped ?? shared?.Scoped ?? DefaultScoped);
    }

    /// <summary>
    /// Whether ANY agent in the document composes to a knowledge block at all.
    /// </summary>
    /// <param name="agents">The <c>agents:</c> section.</param>
    public static bool AnyDeclared(AgentsConfiguration agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        return agents.Items.Any(agent => Compose(agents.Defaults, agent) is not null);
    }

    /// <summary>
    /// Whether ANY agent in the document composes to a scoped search.
    /// </summary>
    /// <param name="agents">The <c>agents:</c> section.</param>
    /// <returns>
    /// <see langword="true"/> when at least one agent's composed knowledge is scoped. An agent with
    /// no composed knowledge at all declares nothing about scoping, so it cannot make this true —
    /// vacuously false over an empty set of participants, the same convention <c>Enumerable.Any</c>
    /// uses.
    /// </returns>
    public static bool AnyScoped(AgentsConfiguration agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        return agents.Items.Any(agent => Compose(agents.Defaults, agent) is { Scoped: true });
    }

    /// <summary>
    /// Whether EVERY agent in the document that composes to a knowledge block composes to a scoped
    /// one.
    /// </summary>
    /// <param name="agents">The <c>agents:</c> section.</param>
    /// <returns>
    /// <see langword="true"/> when no agent with composed knowledge is unscoped. An agent with no
    /// composed knowledge never reads the store, so it does not count against this either way —
    /// vacuously true over an empty set of participants, the same convention <c>Enumerable.All</c>
    /// uses, and the safe direction besides: a store nobody reads is never asked to enforce anything.
    /// </returns>
    public static bool AllScoped(AgentsConfiguration agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        return agents.Items.All(agent => Compose(agents.Defaults, agent) is not { Scoped: false });
    }
}

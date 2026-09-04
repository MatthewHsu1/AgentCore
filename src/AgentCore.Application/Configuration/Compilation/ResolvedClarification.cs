using System.Collections.ObjectModel;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// The document-level ambiguity wiring §7 needs, read once from <c>providers.knowledge</c> and the
/// declared <c>state:</c> slots.
/// </summary>
/// <remarks>
/// The same for every agent, so <see cref="ConfigurationCompiler"/> builds it once rather than
/// letting each agent re-derive it, and both channels — the clarification provider and the agent's
/// own search — read the one instance.
/// </remarks>
public sealed record ResolvedClarification
{
    /// <summary>The wiring of a document that configures no ambiguity: nothing to probe, nothing to ask.</summary>
    public static readonly ResolvedClarification None = new();

    /// <summary>
    /// Gets how the knowledge search asks the caller which value they meant, or <see langword="null"/>
    /// when <c>providers.knowledge.ambiguity</c> is absent — neither the probe nor the clarification
    /// ever runs.
    /// </summary>
    public KnowledgeAmbiguityConfiguration? Ambiguity { get; init; }

    /// <summary>
    /// Gets the <c>providers.knowledge.scope.fromState</c> slots, in declaration order, or empty when
    /// the document names none.
    /// </summary>
    public IReadOnlyList<string> FromState { get; init; } = [];

    /// <summary>
    /// Gets each <see cref="FromState"/> slot's <c>description</c>, keyed by slot name.
    /// </summary>
    /// <remarks>
    /// Every <see cref="FromState"/> slot has a key here. The value is <see langword="null"/> both
    /// where <c>state:</c> declares that slot without a <c>description</c> and where it declares no
    /// such slot at all; readers fall back to the slot name for either.
    /// </remarks>
    public IReadOnlyDictionary<string, string?> SlotDescriptions { get; init; } =
        ReadOnlyDictionary<string, string?>.Empty;

    /// <summary>
    /// Gets the payload value that satisfies any scope on a wildcard facet, or <see langword="null"/>
    /// when <c>providers.knowledge.scope.wildcard</c> is absent.
    /// </summary>
    public string? WildcardValue { get; init; }

    /// <summary>
    /// Gets the facet keys <see cref="WildcardValue"/> widens, or empty when
    /// <c>providers.knowledge.scope.wildcard</c> is absent.
    /// </summary>
    public IReadOnlyList<string> WildcardFacets { get; init; } = [];

    /// <summary>
    /// Gets the payload path each facet key becomes, or <see langword="null"/> when
    /// <c>providers.knowledge.scope.template</c> is absent.
    /// </summary>
    /// <remarks>
    /// The probe walks the resolved path to read a dropped facet's value off a returned card's
    /// <c>Extras</c> (§8 step 5). Boot and the store resolve their own paths from the same document
    /// value through <see cref="ScopeTemplate"/>, which owns the one resolution rule.
    /// </remarks>
    public ScopeTemplate? Template { get; init; }

    /// <summary>Reads the wiring out of one bound document.</summary>
    /// <param name="configuration">The bound document.</param>
    /// <returns>The wiring, or <see cref="None"/> when the document configures no knowledge scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public static ResolvedClarification From(AgentCoreConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var knowledge = configuration.Providers?.Knowledge;
        if (knowledge is null)
        {
            return None;
        }

        var fromState = knowledge.Scope.FromState;

        Dictionary<string, string?> descriptions = new(StringComparer.Ordinal);
        foreach (var slot in fromState)
        {
            descriptions[slot] = configuration.State.TryGetValue(slot, out var declared)
                ? declared.Description
                : null;
        }

        return new ResolvedClarification
        {
            Ambiguity = knowledge.Ambiguity,
            FromState = fromState,
            SlotDescriptions = descriptions,
            WildcardValue = knowledge.Scope.Wildcard?.Value,
            WildcardFacets = knowledge.Scope.Wildcard?.Facets ?? [],
            Template = ScopeTemplate.Parse(knowledge.Scope.Template),
        };
    }
}

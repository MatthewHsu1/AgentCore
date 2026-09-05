using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Turns what the extractor has learned into the scope one turn searches under.
/// </summary>
internal static class StateKnowledgeScope
{
    /// <summary>Composes the turn's scope from the host's ambient and the call's state.</summary>
    /// <param name="state">The state of the call, as it stands at the start of this turn.</param>
    /// <param name="scope">The document's <c>providers.knowledge.scope</c> block, or null.</param>
    /// <param name="ambient">What the host opened for this turn, or null when it opened nothing.</param>
    /// <returns>The scope, or <paramref name="ambient"/> unchanged when this document composes none.</returns>
    internal static KnowledgeScope? Compose(
        StateDocument state, KnowledgeScopeConfiguration? scope, KnowledgeScope? ambient)
    {
        ArgumentNullException.ThrowIfNull(state);

        // fromState without wildcard is refused at boot. wildcard without fromState is not: a
        // deployment may resolve its own facets and want only the wildcard's widening. Either way,
        // with nothing to build from, an unconfigured document composes nothing and the host's own
        // scope — including its absence, which still fails closed — is what the turn sees.
        if (scope?.Wildcard is not { } wildcard || scope.FromState.Count == 0)
        {
            return ambient;
        }

        Dictionary<string, string> facets = new(StringComparer.Ordinal);
        Dictionary<string, KnowledgeFacetOrigin> origins = new(StringComparer.Ordinal);

        if (ambient is not null)
        {
            foreach (var (key, value) in ambient.Facets)
            {
                facets[key] = value;
                origins[key] = KnowledgeFacetOrigin.Host;
            }
        }

        foreach (var slot in scope.FromState)
        {
            if (facets.ContainsKey(slot))
            {
                continue;
            }

            // An unknown key holds the wildcard rather than being left out: an absent key puts no
            // condition on that facet, which admits every value of it.
            if (Known(state, slot) is { } known)
            {
                facets[slot] = known;
                origins[slot] = KnowledgeFacetOrigin.Extractor;
            }
            else
            {
                facets[slot] = wildcard.Value;
                origins[slot] = KnowledgeFacetOrigin.Wildcard;
            }
        }

        return new KnowledgeScope { Facets = facets, Origins = origins };
    }

    /// <summary>Reads one slot, or null when no writer has filled it with a usable value.</summary>
    /// <remarks>
    /// <see cref="StateDocument.Read"/> answers with the declared default for an unfilled slot, so
    /// the emptiness check has to come first. It reports an undeclared name as filled, which is why
    /// the value is checked too.
    /// </remarks>
    private static string? Known(StateDocument state, string slot)
    {
        if (state.IsUnfilled(slot))
        {
            return null;
        }

        return state.Read(slot) is JsonValue value
            && value.TryGetValue(out string? text)
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;
    }
}

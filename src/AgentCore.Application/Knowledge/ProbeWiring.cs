using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Knowledge
{
    /// <summary>The ambiguity wiring the probe runs on, with every part it needs present.</summary>
    /// <param name="Ambiguity">How the search asks the caller which value they meant.</param>
    /// <param name="WildcardValue">The payload value that satisfies any scope on a wildcard facet.</param>
    /// <param name="WildcardFacets">The facet keys <paramref name="WildcardValue"/> widens.</param>
    /// <param name="Template">The payload path each facet key becomes.</param>
    /// <param name="FromState">The <c>fromState</c> slots, in declaration order.</param>
    /// <param name="SlotDescriptions">Each slot's <c>description</c>, keyed by slot name.</param>
    internal sealed record ProbeWiring(
        KnowledgeAmbiguityConfiguration Ambiguity,
        string WildcardValue,
        IReadOnlyList<string> WildcardFacets,
        ScopeTemplate Template,
        IReadOnlyList<string> FromState,
        IReadOnlyDictionary<string, string?> SlotDescriptions)
    {
        /// <summary>Reads the probe's wiring off the document's, or answers <see langword="null"/> when the probe cannot run.</summary>
        /// <remarks>
        /// K19: with no <c>ambiguity:</c> configured (and so no wildcard, since the validator requires one
        /// alongside the other) there is nothing for the probe to drop, and behaviour stays byte-identical
        /// to the wildcard plan's own "holds nothing" notice.
        /// </remarks>
        public static ProbeWiring? From(ResolvedClarification wiring)
        {
            return wiring is { Ambiguity: { } ambiguity, WildcardValue: { } wildcardValue, WildcardFacets: { Count: > 0 } wildcardFacets, Template: { } template }
                        ? new(ambiguity, wildcardValue, wildcardFacets, template, wiring.FromState, wiring.SlotDescriptions)
                        : null;
        }
    }
}

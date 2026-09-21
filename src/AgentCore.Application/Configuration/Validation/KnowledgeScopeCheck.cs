using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>Check 2, knowledge scope: a state-built scope must not produce a filter nobody meant.</summary>
    /// <remarks>
    /// These are document checks, not store checks: <c>KnowledgeStartup</c> skips the store factory
    /// for a host-supplied port, but the session composes the scope either way.
    /// </remarks>
    internal static class KnowledgeScopeCheck
    {
        public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
        {
            if (configuration.Providers?.Knowledge?.Scope is not { } scope)
            {
                return;
            }

            KnowledgeFilterValidator.Check(scope, errors);

            if (scope.Wildcard is { } wildcard)
            {
                CheckWildcardShape(wildcard, errors);
            }

            // A wildcard without fromState is a supported shape: the deployment resolves its own facets
            // and passes them as the conversation's scope, which the store still widens. See
            // StateKnowledgeScope.Compose and KnowledgeStartup's K19 branch. Only the reverse is refused.
            if (scope.FromState.Count == 0)
            {
                return;
            }

            CheckWildcardCoversFromState(scope, errors);

            if (configuration.Extractor is null)
            {
                errors.Add(Reference(
                    "/extractor",
                    "fromState names extractor slots and this document declares no extractor, so no slot "
                    + "is ever filled and every conversation searches only what the wildcard admits."));
            }

            foreach (string name in scope.FromState)
            {
                if (configuration.State.TryGetValue(name, out StateSlotConfiguration? slot))
                {
                    CheckFacetSlot(name, slot, errors);
                }
                else
                {
                    errors.Add(Reference(
                        ValidationPointer.FromState,
                        $"fromState names the slot '{name}', which this document does not declare."));
                }
            }
        }

        private static void CheckWildcardShape(KnowledgeWildcardConfiguration wildcard, List<ConfigurationError> errors)
        {
            if (string.IsNullOrWhiteSpace(wildcard.Value))
            {
                errors.Add(Reference(
                    ValidationPointer.WildcardValue,
                    "the wildcard value is blank, so it names no payload value a card could carry."));
            }

            if (wildcard.Facets.Count == 0)
            {
                errors.Add(Reference(
                    ValidationPointer.WildcardFacets,
                    "the wildcard names no facets, so it widens nothing. Name the reach facets, and "
                    + "never an isolation facet such as a customer id."));
            }
        }

        /// <summary>The wildcard facets and the fromState names must be the same set.</summary>
        private static void CheckWildcardCoversFromState(KnowledgeScopeConfiguration scope, List<ConfigurationError> errors)
        {
            if (scope.Wildcard is not { } widened)
            {
                errors.Add(Reference(
                    ValidationPointer.Wildcard,
                    "fromState is set and no wildcard is. An unknown slot would then leave its facet out "
                    + "of the scope, putting no condition on it, and every value of that facet would be "
                    + "in reach. Declare the wildcard, or drop fromState."));
                return;
            }

            foreach (string? facet in widened.Facets.Where(facet => !scope.FromState.Contains(facet, StringComparer.Ordinal)))
            {
                errors.Add(Reference(
                    ValidationPointer.WildcardFacets,
                    $"wildcard.facets names '{facet}' and fromState does not. The scope filter only "
                    + $"ever puts a condition on a fromState facet, so there is no condition on "
                    + $"'{facet}' for the wildcard to widen."));
            }

            foreach (string? name in scope.FromState.Where(name => !widened.Facets.Contains(name, StringComparer.Ordinal)))
            {
                errors.Add(Reference(
                    ValidationPointer.WildcardFacets,
                    $"fromState names '{name}' and wildcard.facets does not, so an unfilled '{name}' "
                    + "would be searched for the literal wildcard rather than widened by it."));
            }
        }

        /// <summary>A facet slot is a string enum the extractor fills, with no default.</summary>
        private static void CheckFacetSlot(string name, StateSlotConfiguration slot, List<ConfigurationError> errors)
        {
            string pointer = ValidationPointer.State(name);

            if (slot.Default is not null)
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "default"),
                    $"the facet slot '{name}' declares a default. An unfilled slot reads as its "
                    + "default, so every conversation before the caller says otherwise would be scoped to a "
                    + "guess, with no error."));
            }

            if (slot.Type != StateSlotType.String)
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "type"),
                    $"the facet slot '{name}' is not type string. A facet holds one string value."));
            }

            if (slot.Writer != StateWriter.Extractor)
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "writer"),
                    $"the facet slot '{name}' is not written by the extractor. A const slot is filled "
                    + "before turn 1 and would scope every call to it; a tool slot could change the "
                    + "scope mid-conversation."));
            }

            if (slot.EnumValues is not { Count: > 0 })
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "enum"),
                    $"the facet slot '{name}' declares no enum. Nothing would then stop a value the "
                    + "corpus has never been tagged with, which the wildcard turns into an answer "
                    + "from the wrong bucket rather than an empty result."));
            }
        }
    }
}

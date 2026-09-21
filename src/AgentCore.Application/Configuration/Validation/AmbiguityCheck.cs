using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>Check 2, ambiguity: section 10 of the ambiguity design.</summary>
    internal static class AmbiguityCheck
    {
        /// <summary>Refuses an <c>ambiguity:</c> block that could not do what it declares.</summary>
        public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors, List<ConfigurationError> warnings)
        {
            KnowledgeProviderConfiguration? knowledge = configuration.Providers?.Knowledge;

            if (knowledge?.Ambiguity is not { } ambiguity)
            {
                return;
            }

            if (knowledge.Mapper is not null)
            {
                errors.Add(Reference(
                    ValidationPointer.Mapper,
                    "providers.knowledge.ambiguity is declared, and providers.knowledge.mapper names a "
                    + "custom mapper. The probe reads each card's facet values out of Extras, which only "
                    + "the built-in field mapper fills, so every probe would find no values and the "
                    + "channel would stay silent."));
            }

            if (knowledge.Scope.Wildcard is null)
            {
                errors.Add(Reference(
                    ValidationPointer.Ambiguity,
                    "providers.knowledge.ambiguity is declared and providers.knowledge.scope.wildcard is "
                    + "absent. The probe drops a facet the wildcard filled, so with no wildcard it has "
                    + "nothing to drop and the channel can never fire."));
            }

            CheckRanges(ambiguity, errors);

            // A probe drops one of the scope's own facets, so it needs a second one left to search by.
            // Zero is as unreachable as one, and reaches this line whenever the host supplies every facet.
            if ((knowledge.Scope.FromState ?? []).Count <= 1)
            {
                warnings.Add(Reference(
                    ValidationPointer.Ambiguity,
                    "providers.knowledge.ambiguity is declared and scope.fromState names at most one "
                    + "facet. That deployment has no droppable facet other than its only one, so the probe "
                    + "is unreachable unless the host sets one too."));
            }
        }

        private static void CheckRanges(KnowledgeAmbiguityConfiguration ambiguity, List<ConfigurationError> errors)
        {
            CheckRange(
                ambiguity.MaxCandidates,
                2,
                int.MaxValue,
                ConfigurationError.AppendPointer(ValidationPointer.Ambiguity, "maxCandidates"),
                "ambiguity.maxCandidates",
                "Below 2, the ask could never name a spread of candidates.",
                errors);

            CheckRange(
                ambiguity.MaxAsks,
                0,
                int.MaxValue,
                ConfigurationError.AppendPointer(ValidationPointer.Ambiguity, "maxAsks"),
                "ambiguity.maxAsks",
                "0 is legal and means gate only; a negative count is not.",
                errors);

            CheckRange(
                ambiguity.ProbeDeadlineSeconds,
                1,
                MaxIntervalSeconds,
                ConfigurationError.AppendPointer(ValidationPointer.Ambiguity, "probeDeadlineSeconds"),
                "ambiguity.probeDeadlineSeconds",
                "A budget below one second leaves the probe unable to complete even the fastest real "
                + "search, and one above the range throws out of the CancelAfter that arms it.",
                errors);

            CheckRange(
                ambiguity.ProbeWaitMarginSeconds,
                1,
                MaxIntervalSeconds,
                ConfigurationError.AppendPointer(ValidationPointer.Ambiguity, "probeWaitMarginSeconds"),
                "ambiguity.probeWaitMarginSeconds",
                "A margin of 0 reinstates the race it exists to close: the loser's wait would end as the "
                + "winner's own search does, leaving a margin equal to the arrival spread. Above the "
                + "range, the deadline this is added to no longer fits the wait it arms.",
                errors);
        }
    }
}

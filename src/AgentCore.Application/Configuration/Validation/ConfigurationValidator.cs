using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>
    /// Checks 2 to 8 of section 8.5, over one bound document. Each check lives in its own
    /// <c>*Check</c> class in this folder; this type only orders them and shapes the result.
    /// </summary>
    public static class ConfigurationValidator
    {
        /// <summary>Runs checks 2 to 8 and returns everything they find.</summary>
        /// <param name="configuration">The bound document.</param>
        /// <returns>Every error and every partial-coverage warning.</returns>
        public static ConfigurationValidationResult Evaluate(AgentCoreConfiguration configuration)
        {
            ConfigurationValidationResult structural = EvaluateStructure(configuration);

            HashSet<string> declaredToolIds = configuration.Tools.Select(static tool => tool.Id).ToHashSet(StringComparer.Ordinal);
            List<ConfigurationError> toolErrors = [];

            ServedReferenceCheck.Tools(configuration, declaredToolIds, toolErrors);

            return toolErrors.Count == 0
                ? structural
                : new ConfigurationValidationResult
                {
                    Errors = [.. structural.Errors, .. toolErrors],
                    Warnings = structural.Warnings,
                };
        }

        /// <summary>Runs checks 2 to 8 and throws when any of them fails.</summary>
        /// <param name="configuration">The bound document.</param>
        /// <returns>The result, so a caller can read the partial-coverage warnings of check 5.</returns>
        /// <exception cref="ConfigurationLoadException">The document fails one or more checks.</exception>
        public static ConfigurationValidationResult Validate(AgentCoreConfiguration configuration)
        {
            return ThrowOnErrors(Evaluate(configuration));
        }

        /// <summary>
        /// Runs every check of section 8.5 except tool-reference resolution, and returns everything they
        /// find.
        /// </summary>
        /// <param name="configuration">The bound document.</param>
        /// <returns>Every error and every partial-coverage warning.</returns>
        public static ConfigurationValidationResult EvaluateStructure(AgentCoreConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            List<ConfigurationError> errors = [];
            List<ConfigurationError> warnings = [];
            DeclaredNames names = DeclaredNames.From(configuration);

            EntryShapeCheck.Run(configuration, names, errors);
            ReferenceCheck.Run(configuration, names, errors);
            ReasoningTemperatureCheck.Run(configuration, errors);
            SlotWriterCheck.Run(configuration, errors);
            KnowledgeScopeCheck.Run(configuration, errors);
            AmbiguityCheck.Run(configuration, errors, warnings);
            GuardRuleCheck.Run(configuration, errors);
            ExitExclusivityCheck.Run(configuration, errors, warnings);
            ReachabilityCheck.Run(configuration, errors);
            GraphWellFormednessCheck.Run(configuration, errors);
            DelegationCycleCheck.Run(configuration, errors);
            McpCheck.ServerIds(configuration, errors);
            McpCheck.SecretPlacement(configuration, errors);

            return errors.Count == 0 && warnings.Count == 0
                ? ConfigurationValidationResult.Clean
                : new ConfigurationValidationResult
                {
                    Errors = errors,
                    Warnings = warnings,
                };
        }

        /// <summary>
        /// Runs every check of section 8.5 except tool-reference resolution, and throws when any of them
        /// fails.
        /// </summary>
        /// <param name="configuration">The bound document.</param>
        /// <returns>The result, so a caller can read the partial-coverage warnings of check 5.</returns>
        /// <exception cref="ConfigurationLoadException">The document fails one or more checks.</exception>
        public static ConfigurationValidationResult ValidateStructure(AgentCoreConfiguration configuration)
        {
            return ThrowOnErrors(EvaluateStructure(configuration));
        }

        /// <summary>
        /// Resolves every tool reference in the document against the ids the tool registry actually
        /// serves, and throws when one names an id nothing serves.
        /// </summary>
        /// <param name="configuration">The bound document.</param>
        /// <param name="servedToolIds">Every tool id the registry serves, declared and MCP-discovered alike.</param>
        /// <exception cref="ConfigurationLoadException">A reference names a tool id nothing serves.</exception>
        public static void ValidateToolReferences(AgentCoreConfiguration configuration, IReadOnlySet<string> servedToolIds)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(servedToolIds);

            List<ConfigurationError> errors = [];
            ServedReferenceCheck.Tools(configuration, servedToolIds, errors);
            ThrowOnErrors(errors);
        }

        /// <summary>
        /// Resolves every <c>skills:</c> and <c>pinned:</c> entry against the names the bound skills
        /// folder serves.
        /// </summary>
        /// <param name="configuration">The bound document.</param>
        /// <param name="servedSkillNames">Every skill name the bound folder serves.</param>
        /// <exception cref="ConfigurationLoadException">A reference names a skill nothing serves, or a skill is both pinned and loadable.</exception>
        public static void ValidateSkillReferences(AgentCoreConfiguration configuration, IReadOnlySet<string> servedSkillNames)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(servedSkillNames);

            List<ConfigurationError> errors = [];
            ServedReferenceCheck.Skills(configuration, servedSkillNames, errors);
            ServedReferenceCheck.PinnedSkills(configuration, errors);
            ThrowOnErrors(errors);
        }

        /// <summary>
        /// Refuses a declared tool id that the skills provider also registers. The provider's tools are
        /// added per agent and never pass through the tool registry, so a collision is invisible until
        /// the model receives two tools of one name.
        /// </summary>
        /// <param name="configuration">The bound document.</param>
        /// <exception cref="ConfigurationLoadException">A tool id collides with a skills tool name.</exception>
        public static void ValidateSkillToolNames(AgentCoreConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            List<ConfigurationError> errors = [];
            ServedReferenceCheck.SkillToolNames(configuration, errors);
            ThrowOnErrors(errors);
        }

        private static ConfigurationValidationResult ThrowOnErrors(ConfigurationValidationResult result)
        {
            ThrowOnErrors(result.Errors);
            return result;
        }

        private static void ThrowOnErrors(IReadOnlyList<ConfigurationError> errors)
        {
            if (errors.Count > 0)
            {
                throw new ConfigurationLoadException(errors);
            }
        }
    }
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>
    /// Finds the mistakes in a loaded document that the JSON Schema cannot catch, such as a name
    /// that points to nothing or a stage that no path reaches.
    /// </summary>
    public static class ConfigurationValidator
    {
        /// <summary>
        /// Runs <see cref="EvaluateStructure"/>, then checks each tool reference against the tools the
        /// document declares. It never throws on a bad document; <see cref="Validate"/> does.
        /// </summary>
        /// <param name="configuration">The loaded document.</param>
        /// <returns>
        /// The errors, and the warnings. A warning does not stop the load. Some say a check had too
        /// many cases to try them all, so it tried a random sample.
        /// </returns>
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

        /// <summary>Runs <see cref="Evaluate"/> and throws when it finds an error.</summary>
        /// <param name="configuration">The loaded document.</param>
        /// <returns>The result, so the caller can read the warnings.</returns>
        /// <exception cref="ConfigurationLoadException">The document has one or more errors.</exception>
        public static ConfigurationValidationResult Validate(AgentCoreConfiguration configuration)
        {
            return ThrowOnErrors(Evaluate(configuration));
        }

        /// <summary>
        /// Runs every check that needs only the document, and returns what they find. It skips tool
        /// references, because MCP tools are not known until the servers answer.
        /// </summary>
        /// <param name="configuration">The loaded document.</param>
        /// <returns>The errors and the warnings, as <see cref="Evaluate"/> describes them.</returns>
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
            GraphNodeBackgroundCheck.Run(configuration, errors);
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

        /// <summary>Runs <see cref="EvaluateStructure"/> and throws when it finds an error.</summary>
        /// <param name="configuration">The loaded document.</param>
        /// <returns>The result, so the caller can read the warnings.</returns>
        /// <exception cref="ConfigurationLoadException">The document has one or more errors.</exception>
        public static ConfigurationValidationResult ValidateStructure(AgentCoreConfiguration configuration)
        {
            return ThrowOnErrors(EvaluateStructure(configuration));
        }

        /// <summary>
        /// Checks every tool reference against the tool ids the registry serves. Call it after the MCP
        /// servers have listed their tools.
        /// </summary>
        /// <param name="configuration">The loaded document.</param>
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
        /// Checks every <c>skills:</c> and <c>pinned:</c> entry against the skills in the skills folder.
        /// </summary>
        /// <param name="configuration">The loaded document.</param>
        /// <param name="servedSkillNames">Every skill name in the skills folder.</param>
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
        /// Refuses a declared tool id that has the same name as a skills tool. Skills tools do not go
        /// through the tool registry, so without this check the model would get two tools with one name.
        /// </summary>
        /// <param name="configuration">The loaded document.</param>
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

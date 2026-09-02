using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Skills;

/// <summary>Builds the skills provider one agent binds.</summary>
internal static class SkillsProviderFactory
{
    /// <summary>
    /// The stock MAF wording with the run_skill_script step removed and a closing sentence that
    /// says why. The <c>{skills}</c> placeholder is required: the provider's constructor throws
    /// without it, and MAF's own copy of that token is private, so nothing checks this string at
    /// compile time.
    /// </summary>
    private const string InstructionPrompt = """
        You have access to skills containing domain-specific knowledge and capabilities.
        Each skill provides specialized instructions, reference documents, and assets for specific tasks.

        <available_skills>
        {skills}
        </available_skills>

        When a task aligns with a skill's domain, follow these steps in exact order:
        - Use `load_skill` to retrieve the skill's instructions.
        - Follow the provided guidance.
        - Use `read_skill_resource` to read any referenced resources, using the name exactly as listed
          (e.g. `"style-guide"` not `"style-guide.md"`, `"references/FAQ.md"` not `"FAQ.md"`).
        Only load what is needed, when it is needed.
        This host runs no skill scripts. Ignore any scripts a skill lists.
        """;

    /// <summary>Builds the provider one agent binds.</summary>
    /// <param name="catalog">The skills the host bound, shared by every agent.</param>
    /// <param name="skills">The names this agent may load.</param>
    /// <param name="loggers">Where the provider writes its diagnostics, or <see langword="null"/>.</param>
    /// <returns>The provider to hang on that agent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    internal static AIContextProvider Create(
        SkillCatalog catalog,
        IReadOnlyList<string> skills,
        ILoggerFactory? loggers)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(skills);

        HashSet<string> allowed = new(skills, StringComparer.Ordinal);

        AgentSkillsProviderOptions options = new()
        {
            // Nothing answers an approval request mid-call, so an approval-gated tool would stall
            // the turn. The script tool keeps its approval, and the wrapper removes it entirely.
            DisableLoadSkillApproval = true,
            DisableReadSkillResourceApproval = true,
            SkillsInstructionPrompt = InstructionPrompt,
        };

        // The filter is never disposed on purpose: disposal cascades into the shared source, which
        // every other agent is still reading. ownsSource: false stops the provider doing the same.
        return new ReadOnlySkillsProvider(
            new AgentSkillsProvider(
                new FilteringAgentSkillsSource(
                    catalog.Source,
                    (skill, _) => allowed.Contains(skill.Frontmatter.Name),
                    loggers),
                options,
                loggers,
                ownsSource: false));
    }
}

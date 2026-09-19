using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// Check 2, tool ids and skill names: the references only a registry or a bound folder can satisfy.
/// Decision 15 keeps these apart from <see cref="ReferenceCheck"/> because an MCP server's
/// discovery is the only thing that can satisfy a tool reference, so this half has to wait for it.
/// </summary>
internal static class ServedReferenceCheck
{
    /// <summary>Resolves every <c>from:</c> state slot and every agent's <c>tools:</c> entry against what is served.</summary>
    public static void Tools(AgentCoreConfiguration configuration, IReadOnlySet<string> servedToolIds, List<ConfigurationError> errors)
    {
        foreach (var slot in configuration.State)
        {
            if (slot.Value.From is { } from && !servedToolIds.Contains(from.ToolId))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(ValidationPointer.State(slot.Key), "from"),
                    UnservedTool(from.ToolId)));
            }
        }

        var items = configuration.Agents.Items;
        for (var index = 0; index < items.Count; index++)
        {
            var agent = items[index];
            for (var slot = 0; slot < agent.Tools.Count; slot++)
            {
                if (!servedToolIds.Contains(agent.Tools[slot]))
                {
                    errors.Add(Reference(ValidationPointer.AgentTool(index, slot), UnservedTool(agent.Tools[slot])));
                }
            }
        }
    }

    /// <summary>Resolves every agent's <c>skills:</c> and <c>pinned:</c> entry against what the bound folder serves.</summary>
    public static void Skills(AgentCoreConfiguration configuration, IReadOnlySet<string> servedSkillNames, List<ConfigurationError> errors)
    {
        var served = string.Join(", ", servedSkillNames.Order(StringComparer.Ordinal));
        var items = configuration.Agents.Items;

        for (var index = 0; index < items.Count; index++)
        {
            var agent = items[index];
            SkillNames(agent.Skills, "skills", index, servedSkillNames, served, errors);
            SkillNames(agent.Pinned, "pinned", index, servedSkillNames, served, errors);
        }
    }

    /// <summary>
    /// Refuses a skill that is both pinned and loadable. Its body would sit in the prompt and be
    /// offered to <c>load_skill</c> at once, and the second copy is pure cost.
    /// </summary>
    public static void PinnedSkills(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        var items = configuration.Agents.Items;

        for (var index = 0; index < items.Count; index++)
        {
            var agent = items[index];
            var loadable = new HashSet<string>(agent.Skills, StringComparer.Ordinal);

            for (var slot = 0; slot < agent.Pinned.Count; slot++)
            {
                if (!loadable.Contains(agent.Pinned[slot]))
                {
                    continue;
                }

                errors.Add(Reference(
                    ConfigurationError.AppendPointer(
                        ConfigurationError.AppendPointer(ValidationPointer.Agent(index), "pinned"), slot),
                    $"the skill '{agent.Pinned[slot]}' is both pinned and in the skills: list. "
                    + "A pinned skill is already in the prompt, so drop it from skills:."));
            }
        }
    }

    private static void SkillNames(
        IReadOnlyList<string> names,
        string key,
        int agentIndex,
        IReadOnlySet<string> servedSkillNames,
        string served,
        List<ConfigurationError> errors)
    {
        for (var slot = 0; slot < names.Count; slot++)
        {
            if (servedSkillNames.Contains(names[slot]))
            {
                continue;
            }

            errors.Add(Reference(
                ConfigurationError.AppendPointer(
                    ConfigurationError.AppendPointer(ValidationPointer.Agent(agentIndex), key), slot),
                $"the skill '{names[slot]}' is not in the bound skills folder. "
                + $"The folder serves: {served}."));
        }
    }

    /// <summary>
    /// Refuses a declared tool id that the skills provider registers under the same name. The
    /// provider's tools are added per agent and never pass through the tool registry, so a
    /// collision is invisible until the model receives two tools of one name.
    /// </summary>
    public static void SkillToolNames(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        var items = configuration.Agents.Items;
        if (!items.Any(agent => agent.Skills.Count > 0))
        {
            return;
        }

        string[] reserved =
        [
            AgentSkillsProvider.LoadSkillToolName,
            AgentSkillsProvider.ReadSkillResourceToolName,
            AgentSkillsProvider.RunSkillScriptToolName,
        ];

        var tools = configuration.Tools;
        for (var index = 0; index < tools.Count; index++)
        {
            if (!reserved.Contains(tools[index].Id, StringComparer.Ordinal))
            {
                continue;
            }

            errors.Add(Reference(
                ConfigurationError.AppendPointer(ValidationPointer.Tool(index), "id"),
                $"the tool id '{tools[index].Id}' is reserved while any agent declares a skills: "
                + "list, because the skills provider registers a tool of that name. Rename the tool."));
        }
    }

    private static string UnservedTool(string toolId)
        => $"nothing serves the tool '{toolId}'. Declare it in tools:, or check that an mcp: server offers it.";
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>Check 2, entry shapes: each entry holds exactly one of <c>agent:</c>, <c>policy:</c>, or <c>graph:</c>.</summary>
internal static class EntryShapeCheck
{
    public static void Run(AgentCoreConfiguration configuration, DeclaredNames names, List<ConfigurationError> errors)
    {
        if (configuration.Agents.Items.Count == 0)
        {
            errors.Add(Reference(
                "/agents/items",
                "the document declares no agents:, so no entry can resolve an agent."));
        }

        if (configuration.Entries.Count == 0)
        {
            errors.Add(Reference(
                "/entries",
                "the document declares no entries:, so it compiles to nothing."));
            return;
        }

        foreach (var (name, entry) in configuration.Entries)
        {
            var pointer = ValidationPointer.Entry(name);

            CheckShapeCount(name, entry, pointer, errors);
            CheckAgent(name, entry.Agent, pointer, names, errors);
            CheckReply(name, entry.FallbackReply, "fallbackReply", "a failed turn", pointer, errors);
            CheckReply(name, entry.RefusalReply, "refusalReply", "a refused turn", pointer, errors);
        }
    }

    private static void CheckShapeCount(string name, EntryConfiguration entry, string pointer, List<ConfigurationError> errors)
    {
        var shapes = (entry.Agent is null ? 0 : 1) + (entry.Policy is null ? 0 : 1) + (entry.Graph is null ? 0 : 1);

        if (shapes == 0)
        {
            errors.Add(Reference(
                pointer,
                $"the entry '{name}' declares none of agent:, policy:, or graph:. It holds exactly one."));
        }
        else if (shapes > 1)
        {
            errors.Add(Reference(
                pointer,
                $"the entry '{name}' declares more than one of agent:, policy:, and graph:. It holds exactly one."));
        }
    }

    private static void CheckAgent(string name, string? agentId, string pointer, DeclaredNames names, List<ConfigurationError> errors)
    {
        if (agentId is null)
        {
            return;
        }

        var field = ConfigurationError.AppendPointer(pointer, ValidationPointer.AgentField);

        if (string.IsNullOrWhiteSpace(agentId))
        {
            errors.Add(Reference(field, $"the entry '{name}' names an empty agent:. It names one id from agents.items."));
        }
        else if (!names.Agents.Contains(agentId))
        {
            errors.Add(Reference(field, $"the agent '{agentId}' is not declared in agents.items"));
        }
    }

    private static void CheckReply(string name, string? reply, string field, string turn, string pointer, List<ConfigurationError> errors)
    {
        if (reply is not null && string.IsNullOrWhiteSpace(reply))
        {
            errors.Add(Reference(
                ConfigurationError.AppendPointer(pointer, field),
                $"the entry '{name}' sets a blank {field}, so {turn} would speak nothing."));
        }
    }
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.

/// <summary>
/// The provider behind one agent's <c>background:</c> list: its children, each wrapped so a child
/// session knows which conversation started it.
/// </summary>
internal static class AgentBackgroundCompiler
{
    /// <summary>Builds the provider that starts this agent's background children.</summary>
    /// <param name="item">The agent being compiled.</param>
    /// <param name="pointer">This agent's JSON pointer, for a <c>background:</c> failure.</param>
    /// <param name="resolve">Resolves an <c>agents.items</c> id to its compiled agent, or <see langword="null"/> when undeclared.</param>
    public static BackgroundAgentsProvider Build(
        AgentConfiguration item,
        string pointer,
        Func<string, AIAgent?> resolve)
    {
        if (item.Background.Contains(item.Id, StringComparer.Ordinal))
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "background"),
                $"the agent '{item.Id}' names itself in background:, so it would start itself as its own child. "
                + "Remove it from the list, or point at another agent.");
        }

        List<AIAgent> children = new(item.Background.Count);
        for (var index = 0; index < item.Background.Count; index++)
        {
            var childPointer = ConfigurationError.AppendPointer(
                ConfigurationError.AppendPointer(pointer, "background"), index);

            var childId = item.Background[index];

            var child = resolve(childId)
                ?? throw ConfigurationCompiler.Fail(
                    childPointer,
                    $"the agent '{childId}' is not declared in agents.items");

            children.Add(new BackgroundChildAgent(child));
        }

        try
        {
            // Children run with their own compiled tools, which carry no approval-required tool
            // today — every harness tool runs with approval off — so a child never stalls waiting
            // for an approval the parent would only see as a completed task with empty output.
            return new BackgroundAgentsProvider(children);
        }
        catch (ArgumentException exception)
        {
            // The provider keys children by name case-insensitively, so two ids that differ only
            // by case collide here rather than at either declaration.
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "background"),
                $"the agent '{item.Id}' declares background: children whose names collide: {exception.Message}");
        }
    }
}

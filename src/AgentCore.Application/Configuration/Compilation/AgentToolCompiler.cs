using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace AgentCore.Application.Configuration.Compilation;

internal static class AgentToolCompiler
{
    public static List<AITool>? Build(
        AgentConfiguration item,
        ModelReference? model,
        Dictionary<string, ToolConfiguration> declared,
        AgentCompilationContext context,
        string pointer,
        Func<string, AIAgent?> resolveAgent)
    {
        if (item.Tools.Count == 0)
        {
            return null;
        }

        List<AITool> tools = [];
        for (var index = 0; index < item.Tools.Count; index++)
        {
            var id = item.Tools[index];

            var toolPointer = ConfigurationError.AppendPointer(
                ConfigurationError.AppendPointer(pointer, "tools"), index);

            if (!declared.TryGetValue(id, out var tool))
            {
                if (context.Tools is { } discovered && discovered.Contains(id))
                {
                    Add(tools, discovered.Resolve(id), item.Id, id, model, context);
                    continue;
                }

                if (context.Tools is null)
                {
                    // No factory, so nothing this loop could have built anyway.
                    continue;
                }

                throw ConfigurationCompiler.Fail(toolPointer, $"the tool id '{id}' is not declared in tools:, and no tool source serves it.");
            }

            if (tool.Kind == ToolKind.Agent)
            {
                // A kind: agent tool needs no tool factory. Section 7 says section 8 adds no port,
                // and this kind adds none either: the inner agent is already in the document.
                Add(tools, AgentDelegationTool.Create(tool, ResolveInner(tool, resolveAgent, toolPointer)), item.Id, id, model, context);
                continue;
            }

            if (context.Tools is { } registry)
            {
                if (!registry.Contains(id))
                {
                    throw ConfigurationCompiler.Fail(toolPointer, $"the tool id '{id}' is declared, and no tool source serves it.");
                }

                Add(tools, registry.Resolve(id), item.Id, id, model, context);
            }
        }

        return tools.Count == 0 ? null : tools;
    }

    /// <summary>Adds one tool, unless it is a hosted search this agent's model cannot run.</summary>
    /// <remarks>
    /// Tested by type and not by the builtin name, so a hosted search tool a host supplies through
    /// its own <c>IToolSource</c> is covered by the same rule.
    /// </remarks>
    private static void Add(
        List<AITool> tools,
        AITool tool,
        string agentId,
        string toolId,
        ModelReference? model,
        AgentCompilationContext context)
    {
        if (tool is HostedWebSearchTool && !context.ChatClients.SupportsHostedWebSearch(model))
        {
            var logger = context.Loggers?.CreateLogger(typeof(AgentToolCompiler)) ?? NullLogger.Instance;
            var modelDescription = model is { Ref.Length: > 0 } ? $"the model '{model.Ref}'" : "this agent's default model";
            Log.HostedWebSearchDropped(logger, agentId, toolId, modelDescription);
            return;
        }

        tools.Add(tool);
    }

    private static AIAgent ResolveInner(ToolConfiguration tool, Func<string, AIAgent?> resolveAgent, string pointer)
    {
        if (tool.Agent is not { Length: > 0 } id)
        {
            throw ConfigurationCompiler.Fail(pointer, $"the tool '{tool.Id}' is kind: agent and names no agent:.");
        }

        return resolveAgent(id)
            ?? throw ConfigurationCompiler.Fail(
                pointer,
                $"the tool '{tool.Id}' delegates to the agent '{id}', which agents.items does not declare.");
    }
}

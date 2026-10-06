using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Tools.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace AgentCore.Application.Configuration.Compilation
{
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal static class AgentToolCompiler
    {
        public static List<AITool>? Build(
            AgentConfiguration item,
            ModelReference? model,
            Dictionary<string, ToolConfiguration> declared,
            AgentCompilationContext context,
            string pointer,
            Func<string, AIAgent?> resolveAgent,
            IReadOnlyList<BackgroundAgentsProvider> backgroundProviders)
        {
            if (item.Tools.Count == 0)
            {
                return null;
            }

            List<AITool> tools = [];
            for (int index = 0; index < item.Tools.Count; index++)
            {
                string id = item.Tools[index];
                string toolPointer = ConfigurationError.AppendPointer(
                    ConfigurationError.AppendPointer(pointer, "tools"), index);

                if (Resolve(id, declared, context.Tools, resolveAgent, backgroundProviders, toolPointer) is { } tool)
                {
                    Add(tools, tool, item.Id, id, model, context);
                }
            }

            return tools.Count == 0 ? null : tools;
        }

        /// <summary>Finds the tool one id names, or <see langword="null"/> when no source can build it.</summary>
        private static AITool? Resolve(
            string id,
            Dictionary<string, ToolConfiguration> declared,
            ToolRegistry? registry,
            Func<string, AIAgent?> resolveAgent,
            IReadOnlyList<BackgroundAgentsProvider> backgroundProviders,
            string pointer)
        {
            if (!declared.TryGetValue(id, out ToolConfiguration? tool))
            {
                return Undeclared(id, registry, pointer);
            }

            if (tool.Kind == ToolKind.Agent)
            {
                return AgentDelegationTool.Create(tool, ResolveInner(tool, resolveAgent, pointer), backgroundProviders);
            }

            return Declared(id, registry, pointer);
        }

        /// <summary>An id that tools: does not declare. Only a registry that serves it can build it.</summary>
        private static AITool? Undeclared(string id, ToolRegistry? registry, string pointer)
        {
            if (registry is null)
            {
                return null;
            }

            return registry.Contains(id)
                ? registry.Resolve(id)
                : throw ConfigurationCompiler.Fail(pointer, $"the tool id '{id}' is not declared in tools:, and no tool source serves it.");
        }

        /// <summary>An id that tools: declares. The registry, when there is one, has to serve it.</summary>
        private static AITool? Declared(string id, ToolRegistry? registry, string pointer)
        {
            return registry switch
            {
                null => null,
                _ when registry.Contains(id) => registry.Resolve(id),
                _ => throw ConfigurationCompiler.Fail(pointer, $"the tool id '{id}' is declared, and no tool source serves it."),
            };
        }

        /// <summary>Adds one tool, unless it is a hosted marker its agent's model cannot run.</summary>
        private static void Add(
            List<AITool> tools,
            AITool tool,
            string agentId,
            string toolId,
            ModelReference? model,
            AgentCompilationContext context)
        {
            if (tool is HostedWebSearchTool)
            {
                if (context.ChatClients.ResolveHostedTool(tool, model) is not { } resolved)
                {
                    ILogger logger = context.Loggers?.CreateLogger(typeof(AgentToolCompiler)) ?? NullLogger.Instance;

                    string modelDescription = model is { Ref.Length: > 0 } ? $"the model '{model.Ref}'" : "this agent's default model";

                    Log.HostedToolDropped(logger, agentId, toolId, tool.GetType().Name, modelDescription);

                    return;
                }

                tools.Add(resolved);
                return;
            }

            tools.Add(tool);
        }

        private static AIAgent ResolveInner(ToolConfiguration tool, Func<string, AIAgent?> resolveAgent, string pointer)
        {
            return tool.Agent is not { Length: > 0 } id
                ? throw ConfigurationCompiler.Fail(pointer, $"the tool '{tool.Id}' is kind: agent and names no agent:.")
                : resolveAgent(id)
                ?? throw ConfigurationCompiler.Fail(
                    pointer,
                    $"the tool '{tool.Id}' delegates to the agent '{id}', which agents.items does not declare.");
        }
    }
#pragma warning restore MAAI001
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Shipped;

/// <summary>
/// Turns one shipped agent into the function the outer agent calls.
/// </summary>
/// <remarks>
/// The agent goes onto the same <c>AsAIFunction()</c> path <c>kind: agent</c> already uses, so
/// there is one code path for every agent-as-tool.
/// </remarks>
internal static class ShippedAgentBuilder
{
    /// <summary>Builds the function one shipped agent is advertised as.</summary>
    /// <param name="definition">The shipped agent.</param>
    /// <param name="tool">The declaration the document holds.</param>
    /// <param name="ports">The adapters the host bound.</param>
    /// <returns>The function.</returns>
    /// <exception cref="ConfigurationLoadException">A port this agent reads is unbound.</exception>
    internal static AIFunction Build(
        IShippedAgentDefinition definition, ToolConfiguration tool, BuiltinToolPorts ports)
    {
        if (definition.MissingPort(ports) is { } missing)
        {
            throw ToolSourceError.Fail(
                $"the tool '{tool.Id}' is kind: builtin and uses: '{definition.Name}', which reads {missing}, "
                + "and no adapter binds that port. Bind one, or take the tool out of the document.");
        }

        if (ports.ChatClients is not { } clients)
        {
            throw ToolSourceError.Fail(
                $"the tool '{tool.Id}' is kind: builtin and uses: '{definition.Name}', which is an agent and "
                + "reads IChatClientFactory, and no adapter binds that port. Call options.UseChatClients(...).");
        }

        var rounds = tool.MaxRounds ?? definition.DefaultMaxRounds;

        var agent = new ChatClientAgent(
            clients.GetChatClient(tool.Model)
                   .AsBuilder()
                   .UseFunctionInvocation(configure: invoking => invoking.MaximumIterationsPerRequest = rounds)
                   .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                   .Build(),
            new ChatClientAgentOptions
            {
                Name = tool.Id,
                ChatOptions = new ChatOptions
                {
                    Instructions = definition.Instructions,
                    Tools = [.. definition.InnerTools(tool)],
                    ToolMode = ChatToolMode.RequireAny,
                },
            });

        return agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = tool.Id,
            Description = tool.Description ?? definition.DefaultDescription,
            ExcludeResultSchema = true,
        });
    }
}

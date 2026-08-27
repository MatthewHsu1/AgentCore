using System.Text.Json;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Shipped;

/// <summary>
/// Turns one shipped agent into the function the outer agent calls.
/// </summary>
internal static class ShippedAgentBuilder
{
    /// <summary>Builds the function one shipped agent is advertised as.</summary>
    internal static AIFunction Build(
        IShippedAgentDefinition definition, ToolConfiguration tool, BuiltinToolPorts ports)
    {
        if (definition.MissingPort(ports) is { } missing)
        {
            throw BuiltinToolSource.Unbound(tool, definition.Name, missing);
        }

        if (ports.ChatClients is not { } clients)
        {
            throw ToolSourceError.Fail(
                $"the tool '{tool.Id}' is kind: builtin and uses: '{definition.Name}', which is an agent and "
                + "reads IChatClientFactory, and no adapter binds that port. Call options.UseChatClients(...).");
        }

        var described = BuiltinToolSource.Described(tool, definition);
        var rounds = tool.MaxRounds ?? definition.DefaultMaxRounds;

        var agent = new ChatClientAgent(
            new AuditingFunctionInvokingChatClient(
                clients.GetChatClient(tool.Model)
                       .AsBuilder()
                       .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                       .Use(static innerClient => new ModelFacingChatClient(innerClient))
                       .Build())
            {
                MaximumIterationsPerRequest = rounds,
            },
            new ChatClientAgentOptions
            {
                Name = tool.Id,
                ChatOptions = new ChatOptions
                {
                    Instructions = definition.Instructions,
                    Tools = [.. definition.InnerTools(tool, ports)],
                    ToolMode = ChatToolMode.RequireAny,
                },
            });

        return new SpentRoundsAreAnError(
            agent.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = tool.Id,
                Description = described.Description!,
                ExcludeResultSchema = true,
            }),
            tool.Id,
            rounds);
    }

    /// <summary>
    /// Answers the outer agent a section 8.7 error when the inner agent finished with no words.
    /// </summary>
    private sealed class SpentRoundsAreAnError : DelegatingAIFunction
    {
        private readonly string _toolId;

        private readonly int _rounds;

        internal SpentRoundsAreAnError(AIFunction inner, string toolId, int rounds)
            : base(inner)
        {
            _toolId = toolId;
            _rounds = rounds;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);

            return HasText(result)
                ? result
                : ToolErrorResult.Create(
                    _toolId,
                    $"'{_toolId}' used all {_rounds} of its rounds and finished with nothing to report, so "
                    + "assume none of it happened. Tell the caller in words, or ask again for something simpler.");
        }

        /// <summary>Reports whether one <c>AsAIFunction</c> result carries words the outer agent can read.</summary>
        private static bool HasText(object? result)
            => result switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                JsonElement { ValueKind: JsonValueKind.String } element
                    => !string.IsNullOrWhiteSpace(element.GetString()),
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => false,
                _ => true,
            };
    }
}

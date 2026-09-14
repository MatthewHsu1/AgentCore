using System.Text.Json;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
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
            new ComposedRequest(
                agent.AsAIFunction(new AIFunctionFactoryOptions
                {
                    Name = tool.Id,
                    Description = described.Description!,
                    ExcludeResultSchema = true,
                }),
                definition,
                tool.Id,
                agent),
            tool.Id,
            rounds);
    }

    /// <summary>
    /// Hands the inner agent the request its definition composes from the outer agent's string.
    /// </summary>
    private sealed class ComposedRequest : DelegatingAIFunction
    {
        /// <summary>The one parameter <c>AsAIFunction</c> advertises.</summary>
        private const string QueryParameter = "query";

        private readonly IShippedAgentDefinition _definition;
        private readonly string _toolId;
        private readonly AIAgent _inner;

        internal ComposedRequest(AIFunction inner, IShippedAgentDefinition definition, string toolId, AIAgent innerAgent)
            : base(inner)
        {
            _definition = definition;
            _toolId = toolId ?? throw new ArgumentNullException(nameof(toolId));
            _inner = innerAgent ?? throw new ArgumentNullException(nameof(innerAgent));
        }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue(QueryParameter, out var value) || Text(value) is not { } query)
            {
                throw new InvalidOperationException(
                    $"'{_toolId}' was called without a '{QueryParameter}' string, so there is no request to compose.");
            }

            var parent = arguments.TryGetValue(TurnInvocation.ArgumentsKey, out var turn) && turn is TurnInvocation p
                ? p
                : null;

            var composed = _definition.Compose(query, TurnResultsOf(arguments));
            var nested = parent is not null
                ? parent with { Clarifications = null, Nested = true }
                : null;
            var offered = parent?.ToolsFor == _toolId ? parent?.Tools : null;

            return DelegatedAgentRun.RunAsync(_inner, composed, nested, offered, cancellationToken);
        }
        /// <summary>Reads what this turn's tools answered out of one call's arguments.</summary>
        private static TurnResults? TurnResultsOf(AIFunctionArguments arguments)
            => arguments.TryGetValue(TurnInvocation.ArgumentsKey, out var filed)
                ? (filed as TurnInvocation)?.Results
                : null;

        private static string? Text(object? value)
            => value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };
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

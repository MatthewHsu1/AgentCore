using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Turns one <see cref="ToolKind.Agent"/> declaration into the function the outer agent calls.
/// </summary>
internal static class AgentDelegationTool
{
    /// <summary>The argument name <c>AsAIFunction()</c> generates when the document declares no schema.</summary>
    private const string DefaultArgument = "query";

    /// <summary>Builds the function that runs one declared agent.</summary>
    /// <param name="tool">The <c>kind: agent</c> declaration.</param>
    /// <param name="inner">The already compiled inner agent.</param>
    /// <returns>The function the outer agent advertises.</returns>
    internal static AIFunction Create(ToolConfiguration tool, AIAgent inner)
    {
        // The document names the function and describes it, because the calling model reads both to
        // decide when to delegate. Without a description it falls back to the inner agent's own.
        // AsAIFunction is the schema donor only — the wrapper below runs the agent directly, so
        // its name, description and parameters stay exactly what the model was built against.
        var function = inner.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = tool.Id,
            Description = tool.Description ?? inner.Description,
        });

        return new DeclaredSchemaFunction(function, tool.Parameters, tool.Id, inner);
    }

    /// <summary>
    /// Advertises the <c>parameters:</c> the document declares, over a function that takes one
    /// string — or the inner function as-is when the document declares none — and runs the
    /// inner agent directly instead of the framework's delegation machinery, so the nested run
    /// carries the turn on its own options like every other run.
    /// </summary>
    private sealed class DeclaredSchemaFunction : DelegatingAIFunction
    {
        private readonly JsonElement _schema;

        private readonly string? _argument;

        private readonly string _toolId;

        private readonly AIAgent _inner;

        internal DeclaredSchemaFunction(AIFunction inner, JsonNode? parameters, string toolId, AIAgent innerAgent)
            : base(inner)
        {
            ArgumentNullException.ThrowIfNull(toolId);

            _toolId = toolId;
            _inner = innerAgent ?? throw new ArgumentNullException(nameof(innerAgent));

            if (parameters is null)
            {
                _schema = inner.JsonSchema;
                _argument = null;
            }
            else
            {
                using var document = JsonDocument.Parse(parameters.ToJsonString());

                _schema = document.RootElement.Clone();
                _argument = FirstProperty(inner.JsonSchema) ?? DefaultArgument;
            }
        }

        public override JsonElement JsonSchema
            => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            var parent = arguments.TryGetValue(TurnInvocation.ArgumentsKey, out var turn) && turn is TurnInvocation p
                ? p
                : null;

            // Schemaless the donor advertises one query string; declared, the payload the
            // document shaped. Either way the nested agent reads words, not arguments.
            string query = _argument is null
                && arguments.TryGetValue(DefaultArgument, out var bare)
                && BareText(bare) is { } text
                    ? text
                    : ToolArgumentJson.ToJsonObject(arguments).ToJsonString();

            // Decided here where the parent turn is in hand: the nested run neither latches
            // the holder (K42) nor records, and only the run under this tool is offered its
            // tools. Without a turn the nested run is bare, as before.
            var nested = parent is not null
                ? parent with { Clarifications = null, Nested = true }
                : null;

            var offered = parent?.ToolsFor == _toolId ? parent?.Tools : null;

            return DelegatedAgentRun.RunAsync(_inner, query, nested, offered, cancellationToken);
        }

        private static string? BareText(object? value)
            => value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };

        /// <summary>
        /// Reads the name of the first declared property of one JSON Schema object.
        /// </summary>
        private static string? FirstProperty(JsonElement schema)
        {
            if (schema.ValueKind is not JsonValueKind.Object
                || !schema.TryGetProperty("properties", out var properties)
                || properties.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            return properties.EnumerateObject().Select(property => property.Name).FirstOrDefault();
        }

    }
}

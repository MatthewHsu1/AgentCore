using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Turns one <see cref="ToolKind.Agent"/> declaration into the function the outer agent calls.
/// </summary>
/// <remarks>
/// <para>
/// The framework already owns this shape: <c>AIAgentExtensions.AsAIFunction()</c> wraps an
/// <c>AIAgent</c> as an <c>AIFunction</c>. The outer agent advertises one function, calls it, and
/// keeps control. The inner agent runs its own tool-calling loop and returns text.
/// </para>
/// <para>
/// The inner agent is the one the compile table already built, so a delegating agent shares it and
/// never compiles a second copy. T44 and done rule 16 make the compiled agent a process singleton,
/// and <see cref="CompiledAgentRegistry"/> keeps it one.
/// </para>
/// <para>
/// Each call gets a fresh session, because agent-as-tool is call and return: the inner run carries
/// no memory of the last delegation. Section 8.7 sets the budgets that follow from that. One
/// delegation costs the outer agent one of its 40 tool rounds, and the inner agent spends its own 40
/// inside that one round.
/// </para>
/// </remarks>
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
        var function = inner.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = tool.Id,
            Description = tool.Description ?? inner.Description,
        });

        return tool.Parameters is { } parameters
            ? new DeclaredSchemaFunction(function, parameters)
            : function;
    }

    /// <summary>
    /// Advertises the <c>parameters:</c> the document declares, over a function that takes one string.
    /// </summary>
    /// <remarks>
    /// <c>AsAIFunction()</c> generates a single string argument. A delegating tool usually wants a
    /// richer call shape, so this wrapper publishes the declared schema and hands the whole argument
    /// object to the inner agent as one compact JSON object. That is one rule and it never changes:
    /// what the model fills is what the inner agent reads.
    /// </remarks>
    private sealed class DeclaredSchemaFunction : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly JsonElement _schema;
        private readonly string _argument;

        internal DeclaredSchemaFunction(AIFunction inner, JsonNode parameters)
        {
            _inner = inner;

            using var document = JsonDocument.Parse(parameters.ToJsonString());
            _schema = document.RootElement.Clone();

            _argument = FirstProperty(inner.JsonSchema) ?? DefaultArgument;
        }

        public override string Name => _inner.Name;

        public override string Description => _inner.Description;

        public override JsonElement JsonSchema => _schema;

        public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;

        public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            JsonObject payload = [];
            foreach (var argument in arguments)
            {
                payload[argument.Key] = ToNode(argument.Value);
            }

            AIFunctionArguments forwarded = new(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [_argument] = payload.ToJsonString(),
            })
            {
                Services = arguments.Services,
                Context = arguments.Context,
            };

            return _inner.InvokeAsync(forwarded, cancellationToken);
        }

        /// <summary>Reads the name of the first declared property of one JSON Schema object.</summary>
        private static string? FirstProperty(JsonElement schema)
        {
            if (schema.ValueKind is not JsonValueKind.Object
                || !schema.TryGetProperty("properties", out var properties)
                || properties.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in properties.EnumerateObject())
            {
                return property.Name;
            }

            return null;
        }

        /// <summary>Carries one tool argument into the JSON object the inner agent reads.</summary>
        private static JsonNode? ToNode(object? value) => value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            _ => JsonValue.Create(value.ToString()),
        };
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
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
    private sealed class DeclaredSchemaFunction : DelegatingAIFunction
    {
        private readonly JsonElement _schema;
        private readonly string _argument;

        internal DeclaredSchemaFunction(AIFunction inner, JsonNode parameters)
            : base(inner)
        {
            using var document = JsonDocument.Parse(parameters.ToJsonString());
            _schema = document.RootElement.Clone();

            _argument = FirstProperty(inner.JsonSchema) ?? DefaultArgument;
        }

        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            var payload = ToolArgumentJson.ToJsonObject(arguments);

            AIFunctionArguments forwarded = new(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [_argument] = payload.ToJsonString(),
            })
            {
                Services = arguments.Services,
                Context = arguments.Context,
            };

            return InnerFunction.InvokeAsync(forwarded, cancellationToken);
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
    }
}

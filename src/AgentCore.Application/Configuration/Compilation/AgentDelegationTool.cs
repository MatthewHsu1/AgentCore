using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>
    /// Turns one <see cref="ToolKind.Agent"/> declaration into the function the outer agent conversations.
    /// </summary>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal static class AgentDelegationTool
    {
        /// <summary>The argument name <c>AsAIFunction()</c> generates when the document declares no schema.</summary>
        private const string DefaultArgument = "query";

        /// <summary>Builds the function that runs one declared agent.</summary>
        /// <param name="tool">The <c>kind: agent</c> declaration.</param>
        /// <param name="inner">The already compiled inner agent.</param>
        /// <param name="backgroundProviders">
        /// Every background provider the document compiled, so the delegation's fresh session releases its
        /// background children when the tool returns.
        /// </param>
        /// <returns>The function the outer agent advertises.</returns>
        internal static AIFunction Create(ToolConfiguration tool, AIAgent inner, IReadOnlyList<BackgroundAgentsProvider> backgroundProviders)
        {
            AIFunction function = inner.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = tool.Id,
                Description = tool.Description ?? inner.Description,
            });

            return new DeclaredSchemaFunction(function, tool.Parameters, tool.Id, inner, backgroundProviders);
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

            private readonly IReadOnlyList<BackgroundAgentsProvider> _backgroundProviders;

            internal DeclaredSchemaFunction(
                AIFunction inner,
                JsonNode? parameters,
                string toolId,
                AIAgent innerAgent,
                IReadOnlyList<BackgroundAgentsProvider> backgroundProviders)
                : base(inner)
            {
                ArgumentNullException.ThrowIfNull(toolId);
                ArgumentNullException.ThrowIfNull(backgroundProviders);

                _toolId = toolId;
                _inner = innerAgent ?? throw new ArgumentNullException(nameof(innerAgent));
                _backgroundProviders = backgroundProviders;

                if (parameters is null)
                {
                    _schema = inner.JsonSchema;
                    _argument = null;
                }
                else
                {
                    using JsonDocument document = JsonDocument.Parse(parameters.ToJsonString());

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

                TurnInvocation? parent = TurnInvocation.FiledIn(arguments);

                string query = _argument is null
                    && arguments.TryGetValue(DefaultArgument, out object? bare)
                    && BareText(bare) is { } text
                        ? text
                        : ToolArgumentJson.ToJsonObject(arguments).ToJsonString();

                TurnInvocation? nested = parent is not null
                    ? parent with { Clarifications = null, Nested = true }
                    : null;

                IReadOnlyList<AITool>? offered = parent?.ToolsFor == _toolId ? parent?.Tools : null;

                return DelegatedAgentRun.RunAsync(_inner, query, nested, offered, _backgroundProviders, cancellationToken);
            }

            private static string? BareText(object? value)
            {
                return value switch
                {
                    string text => text,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                    _ => null,
                };
            }

            /// <summary>
            /// Reads the name of the first declared property of one JSON Schema object.
            /// </summary>
            private static string? FirstProperty(JsonElement schema)
            {
                return schema.ValueKind is not JsonValueKind.Object
                    || !schema.TryGetProperty("properties", out JsonElement properties)
                    || properties.ValueKind is not JsonValueKind.Object
                    ? null
                    : properties.EnumerateObject().Select(property => property.Name).FirstOrDefault();
            }

        }
    }
#pragma warning restore MAAI001
}

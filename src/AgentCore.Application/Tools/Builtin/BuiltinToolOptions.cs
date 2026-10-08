using System.Reflection;
using System.Text.Json;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Binding;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin
{
    /// <summary>
    /// What every built-in hands <see cref="AIFunctionFactory"/>, and the rules they all share.
    /// </summary>
    internal static class BuiltinToolOptions
    {
        /// <summary>Builds the options every built-in is created with.</summary>
        /// <param name="tool">The declaration the document holds.</param>
        /// <returns>The options: the declared id as the name, the resolved description, no result schema,
        /// the turn bound from the conversation rather than filled by the model, and a required text input the model
        /// left out bound as <see langword="null"/>.</returns>
        internal static AIFunctionFactoryOptions Options(ToolConfiguration tool)
        {
            return new()
            {
                Name = tool.Id,
                Description = tool.Description ?? string.Empty,
                ExcludeResultSchema = true,
                ConfigureParameterBinding = Bind,
            };
        }

        private static AIFunctionFactoryOptions.ParameterBindingOptions Bind(ParameterInfo parameter)
        {
            AIFunctionFactoryOptions.ParameterBindingOptions runtime = ToolParameterBindings.For(parameter);
            if (runtime.BindParameter is not null)
            {
                return runtime;
            }

            return parameter.ParameterType == typeof(string) && !parameter.HasDefaultValue
                ? new() { BindParameter = (bound, arguments) => TextOf(bound.Name!, arguments) }
                : default;
        }

        private static string? TextOf(string name, AIFunctionArguments arguments)
        {
            return arguments.TryGetValue(name, out object? value)
                ? value switch
                {
                    null => null,
                    string text => text,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                    JsonElement { ValueKind: JsonValueKind.Null } => null,
                    JsonElement element => element.GetRawText(),
                    _ => value.ToString(),
                }
                : null;
        }
    }
}

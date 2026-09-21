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
        /// and the turn bound from the conversation rather than filled by the model.</returns>
        internal static AIFunctionFactoryOptions Options(ToolConfiguration tool)
        {
            return new()
            {
                Name = tool.Id,
                Description = tool.Description ?? string.Empty,
                ExcludeResultSchema = true,
                ConfigureParameterBinding = ToolParameterBindings.For,
            };
        }
    }
}

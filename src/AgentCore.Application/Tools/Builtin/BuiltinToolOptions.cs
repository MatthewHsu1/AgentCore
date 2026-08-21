using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>
/// What every built-in hands <see cref="AIFunctionFactory"/>, and the two rules they all share.
/// </summary>
internal static class BuiltinToolOptions
{
    /// <summary>Builds the options every built-in is created with.</summary>
    /// <param name="tool">The declaration the document holds.</param>
    /// <returns>The options.</returns>
    internal static AIFunctionFactoryOptions Options(ToolConfiguration tool) => new()
    {
        Name = tool.Id,
        Description = tool.Description ?? string.Empty,
        ExcludeResultSchema = true,
    };
}

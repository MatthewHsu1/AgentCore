using System.Reflection;
using AgentCore.Application.Runtime;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Binding;

// The runtime-filled parameters a bound tool declares. The model never sees them: they bind
// from the turn the invoking client filed in the call's arguments.
internal static class ToolParameterBindings
{
    /// <summary>Binds one runtime-filled parameter, or answers <see langword="default"/> for the model's own.</summary>
    internal static AIFunctionFactoryOptions.ParameterBindingOptions For(ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        var type = parameter.ParameterType;
        return type == typeof(ToolCallScope) || type == typeof(TurnInvocation)
            ? new() { ExcludeFromSchema = true, BindParameter = (_, args) => Bind(type, args) }
            : default;
    }

    private static object? Bind(Type type, AIFunctionArguments? args)
    {
        var invocation = args is not null && args.TryGetValue(TurnInvocation.ArgumentsKey, out var filed)
            ? filed as TurnInvocation
            : null;

        if (type == typeof(TurnInvocation))
        {
            return invocation;
        }

        return invocation is not null
            ? ToolCallScopes.From(invocation)
            : throw new InvalidOperationException(ToolCallScopes.NoTurnMessage);
    }
}

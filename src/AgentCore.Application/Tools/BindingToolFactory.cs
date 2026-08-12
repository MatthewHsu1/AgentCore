using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// Builds the <c>kind: binding</c> tools.
/// </summary>
/// <remarks>
/// The <c>binds:</c> name has to be registered before the document compiles. A host that forgot one
/// gets a startup failure and never an agent that lost a tool without saying so.
/// </remarks>
public sealed class BindingToolFactory : IAgentToolFactory
{
    private readonly ToolBindingRegistry _registry;

    /// <summary>Creates the factory.</summary>
    /// <param name="registry">The delegates the host registered.</param>
    public BindingToolFactory(ToolBindingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>Builds one binding tool.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <returns>The tool, or <see langword="null"/> when the kind is not <see cref="ToolKind.Binding"/>.</returns>
    /// <exception cref="ConfigurationLoadException">The <c>binds:</c> name is not registered.</exception>
    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Kind != ToolKind.Binding)
        {
            return null;
        }

        if (tool.Binds is not { Length: > 0 } name)
        {
            throw Fail($"the tool '{tool.Id}' is kind: binding and names no binds:.");
        }

        if (!_registry.TryGetBinding(name, out var binding) || binding is null)
        {
            throw Fail(
                $"the tool '{tool.Id}' binds to '{name}', which the host did not register. Register the "
                + "delegate on the ToolBindingRegistry before the document compiles.");
        }

        return new BindingTool(tool, binding);
    }

    private static ConfigurationLoadException Fail(string message)
        => new(new ConfigurationError
        {
            Pointer = "/tools",
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });
}

/// <summary>One <c>kind: binding</c> tool.</summary>
internal sealed class BindingTool : DeclaredTool
{
    private readonly ToolBinding _binding;

    internal BindingTool(ToolConfiguration tool, ToolBinding binding)
        : base(tool) => _binding = binding;

    protected override ValueTask<object?> CallAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
        => _binding(ArgumentsAsJson(arguments), cancellationToken);
}

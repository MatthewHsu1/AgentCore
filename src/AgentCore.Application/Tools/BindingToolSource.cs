using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// Serves the <c>kind: binding</c> tools.
/// </summary>
/// <remarks>
/// The <c>binds:</c> name has to be registered before the document compiles. A host that forgot one
/// gets a startup failure and never an agent that lost a tool without saying so.
/// </remarks>
public sealed class BindingToolSource : IToolSource
{
    private readonly ToolBindingRegistry _registry;

    /// <summary>Creates the source.</summary>
    /// <param name="registry">The delegates the host registered.</param>
    public BindingToolSource(ToolBindingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    /// <exception cref="ConfigurationLoadException">A <c>binds:</c> name is missing or not registered.</exception>
    public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
        ToolSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<ToolRegistration> registrations = [];
        foreach (var declared in context.DeclarationsOf(ToolKind.Binding))
        {
            if (declared.Binds is not { Length: > 0 } name)
            {
                throw ToolSourceError.Fail($"the tool '{declared.Id}' is kind: binding and names no binds:.");
            }

            if (!_registry.TryGetBinding(name, out var binding) || binding is null)
            {
                throw ToolSourceError.Fail(
                    $"the tool '{declared.Id}' binds to '{name}', which the host did not register. Register the "
                    + "delegate on the ToolBindingRegistry before the document compiles.");
            }

            var bound = binding;
            registrations.Add(new ToolRegistration(
                declared.Id, declared.Description ?? string.Empty, () => new BindingTool(declared, bound)));
        }

        return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(registrations);
    }
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

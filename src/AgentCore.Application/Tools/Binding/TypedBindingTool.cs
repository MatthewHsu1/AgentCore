using System.Text.Json;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Binding;

/// <summary>
/// One <c>kind: binding</c> tool whose arguments come from a host method's signature.
/// </summary>
internal sealed class TypedBindingTool : DeclaredTool
{
    private readonly AIFunction _inner;

    internal TypedBindingTool(ToolConfiguration tool, Delegate method)
        : base(tool)
        => _inner = AIFunctionFactory.Create(method, tool.Id, tool.Description);

    /// <summary>
    /// Gets the schema the host method's parameters describe. It replaces the base schema, which
    /// reads the document's <c>parameters:</c>, because a typed binding declares none.
    /// </summary>
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override ValueTask<object?> CallAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
        => _inner.InvokeAsync(arguments, cancellationToken);
}

using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools;

/// <summary>
/// One host delegate a <c>kind: binding</c> tool calls.
/// </summary>
/// <param name="arguments">The arguments the model filled, as one JSON object.</param>
/// <param name="cancellationToken">Cancels the call.</param>
/// <returns>
/// The result the model reads. A <see cref="JsonNode"/> reaches the model exactly as it is written.
/// </returns>
/// <remarks>
/// The delegate may throw. The tool turns the failure into an error result, so section 8.7 holds
/// whatever the host does.
/// </remarks>
public delegate ValueTask<object?> ToolBinding(JsonObject arguments, CancellationToken cancellationToken);

/// <summary>
/// The map from a <c>binds:</c> name to the host delegate behind it.
/// </summary>
/// <remarks>
/// <para>
/// The document writes <c>binds: CreateCase</c> and knows nothing else. The host registers
/// <c>CreateCase</c> here at startup, so the binding kind crosses no port and reaches no vendor: it
/// is the seam the host owns.
/// </para>
/// <para>
/// A name is compared with <see cref="StringComparer.Ordinal"/>, the same as every other name in the
/// document. Registration happens at startup and reading happens on a call, so the registry is safe
/// to read from many turns at once once the host stops writing to it.
/// </para>
/// </remarks>
public sealed class ToolBindingRegistry
{
    private readonly Dictionary<string, ToolBinding> _bindings = new(StringComparer.Ordinal);

    /// <summary>Gets the number of names the host registered.</summary>
    public int Count => _bindings.Count;

    /// <summary>Gets the names the host registered.</summary>
    public IReadOnlyCollection<string> Names => _bindings.Keys;

    /// <summary>Registers one host delegate.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes.</param>
    /// <param name="binding">The delegate the tool calls.</param>
    /// <returns>This registry, so a host chains its registrations.</returns>
    /// <exception cref="ArgumentException">The name is already registered.</exception>
    public ToolBindingRegistry Register(string name, ToolBinding binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(binding);

        if (!_bindings.TryAdd(name, binding))
        {
            throw new ArgumentException($"the binding '{name}' is already registered.", nameof(name));
        }

        return this;
    }

    /// <summary>Reports whether one name is registered.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes.</param>
    /// <returns><see langword="true"/> when the host registered the name.</returns>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _bindings.ContainsKey(name);
    }

    /// <summary>Reads the delegate one name points at.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes.</param>
    /// <param name="binding">The delegate, when the name is registered.</param>
    /// <returns><see langword="true"/> when the host registered the name.</returns>
    public bool TryGetBinding(string name, out ToolBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _bindings.TryGetValue(name, out binding);
    }
}

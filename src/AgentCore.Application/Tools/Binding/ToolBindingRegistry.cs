using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools.Binding;

/// <summary>
/// One host delegate a <c>kind: binding</c> tool calls.
/// </summary>
/// <param name="arguments">The arguments the model filled, as one JSON object.</param>
/// <param name="cancellationToken">Cancels the call.</param>
/// <returns>
/// The result the model reads. A <see cref="JsonNode"/> reaches the model exactly as it is written.
/// </returns>
public delegate ValueTask<object?> ToolBinding(JsonObject arguments, CancellationToken cancellationToken);

/// <summary>
/// The map from a <c>binds:</c> name to the host delegate behind it.
/// </summary>
public sealed class ToolBindingRegistry
{
    private readonly Dictionary<string, Delegate> _bindings = new(StringComparer.Ordinal);

    /// <summary>Gets the number of names the host registered.</summary>
    public int Count => _bindings.Count;

    /// <summary>Gets the names the host registered.</summary>
    public IReadOnlyCollection<string> Names => _bindings.Keys;

    /// <summary>Registers one host delegate that reads the arguments as JSON.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes.</param>
    /// <param name="binding">The delegate the tool calls.</param>
    /// <returns>This registry, so a host chains its registrations.</returns>
    /// <exception cref="ArgumentException">The name is already registered.</exception>
    public ToolBindingRegistry Register(string name, ToolBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return Add(name, binding);
    }

    /// <summary>Registers one host method, and its parameters become the tool's arguments.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes.</param>
    /// <param name="method">
    /// The method the tool calls. Its parameters are the arguments the model fills, and their JSON
    /// Schema, so the declaration this name serves must write no <c>parameters:</c>. A
    /// <see cref="System.ComponentModel.DescriptionAttribute"/> on a parameter reaches the model. A
    /// parameter of type <see cref="ToolCallScope"/> is filled by the runtime with the call the turn
    /// belongs to, and is not exposed to the model.
    /// </param>
    /// <returns>This registry, so a host chains its registrations.</returns>
    /// <exception cref="ArgumentException">The name is already registered.</exception>
    public ToolBindingRegistry Register(string name, Delegate method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Add(name, method);
    }

    /// <summary>Reports whether one name is registered, either way.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes.</param>
    /// <returns><see langword="true"/> when the host registered the name.</returns>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _bindings.ContainsKey(name);
    }

    /// <summary>Reads the JSON delegate one name points at.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes.</param>
    /// <param name="binding">The delegate, when the host registered this name as a JSON delegate.</param>
    /// <returns>
    /// <see langword="true"/> when the host registered the name as a <see cref="ToolBinding"/>. A
    /// name the host registered as a typed method answers <see langword="false"/>; read it with
    /// <see cref="TryGetMethod"/>.
    /// </returns>
    public bool TryGetBinding(string name, out ToolBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(name);

        binding = _bindings.TryGetValue(name, out var registered) ? registered as ToolBinding : null;

        return binding is not null;
    }

    /// <summary>Reads the typed method one name points at.</summary>
    /// <param name="name">The name a <c>binds:</c> field writes.</param>
    /// <param name="method">The method, when the host registered this name as a typed method.</param>
    /// <returns>
    /// <see langword="true"/> when the host registered the name as a typed method. A name the host
    /// registered as a <see cref="ToolBinding"/> answers <see langword="false"/>; read it with
    /// <see cref="TryGetBinding"/>.
    /// </returns>
    public bool TryGetMethod(string name, out Delegate? method)
    {
        ArgumentNullException.ThrowIfNull(name);

        method = _bindings.TryGetValue(name, out var registered) && registered is not ToolBinding
            ? registered
            : null;

        return method is not null;
    }

    private ToolBindingRegistry Add(string name, Delegate registered)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!_bindings.TryAdd(name, registered))
        {
            throw new ArgumentException($"the binding '{name}' is already registered.", nameof(name));
        }

        return this;
    }
}

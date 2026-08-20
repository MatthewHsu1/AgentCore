using AgentCore.Application.Ports;

namespace AgentCore.TestSupport;

/// <summary>
/// A resolver that holds a map of names to values, and remembers what it was asked for.
/// </summary>
public sealed class MapSecretResolver : ISecretResolverPort
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Gets every name this resolver was asked for, in call order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Adds one name and its value, and returns this resolver.</summary>
    public MapSecretResolver With(string name, string value)
    {
        _values[name] = value;
        return this;
    }

    /// <inheritdoc />
    public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        Asked.Add(name);
        return ValueTask.FromResult(_values.TryGetValue(name, out var value) ? value : null);
    }
}

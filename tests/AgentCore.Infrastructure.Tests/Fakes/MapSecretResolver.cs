using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Tests.Fakes;

/// <summary>
/// An offline resolver that answers from a map, and records every name it is asked for.
/// </summary>
/// <remarks>
/// This mirrors the resolver fake the application tests use. An adapter resolves its credential at
/// startup, so the recorded names prove which secret an adapter reads and how many times.
/// </remarks>
internal sealed class MapSecretResolver : ISecretResolverPort
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

    public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        Asked.Add(name);
        return ValueTask.FromResult(_values.TryGetValue(name, out var value) ? value : null);
    }
}

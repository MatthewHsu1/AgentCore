using AgentCore.Application.Ports;

namespace AgentCore.Application.Tests.Secrets.Fakes;

/// <summary>
/// An offline resolver that answers from a map, and counts every name it is asked for.
/// </summary>
/// <remarks>
/// The count is the point of most of these tests: AgentCore resolves at startup, so one name costs
/// one read however many tools reference it.
/// </remarks>
internal sealed class MapSecretResolver : ISecretResolverPort
{
    private readonly Dictionary<string, string> _values;

    public MapSecretResolver(params KeyValuePair<string, string>[] values)
    {
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            _values[value.Key] = value.Value;
        }
    }

    /// <summary>Gets every name this resolver was asked for, in call order.</summary>
    public List<string> Asked { get; } = [];

    public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        Asked.Add(name);
        return ValueTask.FromResult(_values.TryGetValue(name, out var value) ? value : null);
    }

    /// <summary>Builds one entry of the map.</summary>
    public static KeyValuePair<string, string> Secret(string name, string value) => new(name, value);
}

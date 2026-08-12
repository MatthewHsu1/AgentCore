using System.Globalization;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Secrets;

/// <summary>
/// Every <c>${secret:name}</c> value one document needs, read once at startup.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ResolveAsync"/> walks the loaded document, collects each distinct name a
/// <see cref="SecretTemplate"/> references, and asks <see cref="ISecretResolverPort"/> for it one
/// time. The tool factory then formats its templates against this set, so a tool call costs no
/// lookup and a missing credential fails before the first call rather than during one.
/// </para>
/// <para>
/// Today one place in the document holds references: <c>tools[].request.headers</c>. The walk is
/// written as a table over the document, so a later section that carries a template joins it in one
/// place.
/// </para>
/// <para>
/// The set reads out values through <see cref="Format(SecretTemplate)"/> and nothing else. It never
/// enumerates them, and <see cref="ToString"/> reports the count alone, because a resolved set lands
/// in a log line sooner or later.
/// </para>
/// </remarks>
public sealed class ResolvedSecrets
{
    private readonly Dictionary<string, string> _values;

    private ResolvedSecrets(Dictionary<string, string> values) => _values = values;

    /// <summary>Gets the set that holds no secret.</summary>
    public static ResolvedSecrets Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Gets the number of names this set holds.</summary>
    public int Count => _values.Count;

    /// <summary>Gets the names this set holds. It never gets the values.</summary>
    public IReadOnlyCollection<string> Names => _values.Keys;

    /// <summary>Builds a set from names a host already holds.</summary>
    /// <param name="secrets">The name and value pairs.</param>
    /// <returns>The set.</returns>
    public static ResolvedSecrets Create(IEnumerable<KeyValuePair<string, string>> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var secret in secrets)
        {
            values[secret.Key] = secret.Value;
        }

        return values.Count == 0 ? Empty : new ResolvedSecrets(values);
    }

    /// <summary>Resolves every reference one loaded document holds.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="resolver">The chain that reads a secret.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The set the tool factory formats against.</returns>
    /// <exception cref="SecretResolutionException">One name resolves to nothing.</exception>
    public static async ValueTask<ResolvedSecrets> ResolveAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(resolver);

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var (template, pointer) in Templates(configuration))
        {
            foreach (var reference in template.References)
            {
                if (values.ContainsKey(reference.Name))
                {
                    // One name costs one read, however many strings reference it.
                    continue;
                }

                var value = await resolver.TryResolveAsync(reference.Name, cancellationToken).ConfigureAwait(false);
                values[reference.Name] = value
                    ?? throw SecretResolutionException.Unresolved(reference.Name, pointer);
            }
        }

        return values.Count == 0 ? Empty : new ResolvedSecrets(values);
    }

    /// <summary>Reports whether the set holds one name.</summary>
    /// <param name="name">The secret name.</param>
    /// <returns><see langword="true"/> when the name resolved.</returns>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _values.ContainsKey(name);
    }

    /// <summary>Rebuilds one configuration string with every reference replaced by its value.</summary>
    /// <param name="template">The template the parser produced.</param>
    /// <returns>The text with no reference left in it.</returns>
    /// <exception cref="SecretResolutionException">The template references a name this set does not hold.</exception>
    public string Format(SecretTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return template.Format(name => _values.TryGetValue(name, out var value)
            ? value
            : throw SecretResolutionException.Unresolved(name));
    }

    /// <summary>Writes how many names the set holds, and never what they are worth.</summary>
    /// <returns>The count, as text.</returns>
    public override string ToString()
        => "ResolvedSecrets(" + _values.Count.ToString(CultureInfo.InvariantCulture) + " names)";

    /// <summary>Walks every string of one document that may hold a reference.</summary>
    private static IEnumerable<(SecretTemplate Template, string Pointer)> Templates(AgentCoreConfiguration configuration)
    {
        for (var index = 0; index < configuration.Tools.Count; index++)
        {
            if (configuration.Tools[index].Request is not { } request)
            {
                continue;
            }

            var headers = ConfigurationError.AppendPointer(
                ConfigurationError.AppendPointer(
                    ConfigurationError.AppendPointer("/tools", index), "request"),
                "headers");

            foreach (var header in request.Headers)
            {
                if (header.Value.HasSecretReferences)
                {
                    yield return (header.Value, ConfigurationError.AppendPointer(headers, header.Key));
                }
            }
        }
    }
}

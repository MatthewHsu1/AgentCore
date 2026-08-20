using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;

namespace AgentCore.Infrastructure.Secrets;

/// <summary>
/// Reads a secret from a process environment variable.
/// </summary>
/// <remarks>
/// <para>
/// It tries the name the document wrote first, then the shouting form of it: <c>orders-api-key</c>
/// becomes <c>ORDERS_API_KEY</c>. A document therefore keeps its own vocabulary, and a deployment
/// keeps the variable names every shell and container runtime already writes.
/// </para>
/// <para>
/// The value never reaches a log. This adapter reads it and hands it straight to
/// <see cref="ResolvedSecrets"/>.
/// </para>
/// </remarks>
public sealed class EnvironmentSecretResolver : ISecretResolverPort
{
    private readonly Func<string, string?> _read;

    /// <summary>Creates a resolver over the process environment.</summary>
    public EnvironmentSecretResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Creates a resolver over any variable source. The tests use this.</summary>
    /// <param name="read">Reads one variable, and answers null when it is not set.</param>
    internal EnvironmentSecretResolver(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    /// <summary>Reads the value of one secret.</summary>
    /// <param name="name">The name the document wrote.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/> when neither form is set.</returns>
    public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();

        if (name.Length == 0)
        {
            return ValueTask.FromResult<string?>(null);
        }

        var value = Empty(_read(name)) ?? Empty(_read(ToVariableName(name)));
        return ValueTask.FromResult(value);
    }

    /// <summary>Turns a document name into the environment variable form of it.</summary>
    /// <param name="name">The name the document wrote.</param>
    /// <returns>The name in upper case, with every separator written as an underscore.</returns>
    public static string ToVariableName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        StringBuilder builder = new(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        }

        return builder.ToString();
    }

    /// <summary>Reads an empty variable as an unset one.</summary>
    private static string? Empty(string? value) => value is { Length: > 0 } ? value : null;
}

namespace AgentCore.Application.Secrets;

/// <summary>
/// One credential, named twice: the way a document writes it and the way a shell writes it.
/// </summary>
/// <remarks>
/// <para>
/// A vendor adapter needs both halves at once. It asks the <c>ISecretResolverPort</c> chain for
/// <see cref="Name"/>, the name a document writes as <c>${secret:name}</c>, and it reads
/// <see cref="VariableName"/> when the chain holds nothing, because a deployment that never wrote a
/// document still has the variable every tool of that vendor already reads. Two loose strings drift
/// apart the day one of them moves, so they travel as one value.
/// </para>
/// <para>
/// A name is not a secret. This type holds what a credential is called and never what it is worth,
/// so it is safe in a message, a log line, and this file.
/// </para>
/// <para>
/// Both halves are required, and the constructor is where that is settled: a pair with a blank half
/// would ask the chain for nothing, or fall back to nothing, and the failure would arrive as a
/// missing key rather than as the typo it is. The two properties are read-only for the same reason,
/// so a pair that exists is a pair that was checked.
/// </para>
/// </remarks>
/// <param name="Name">The name between the colon and the closing brace of <c>${secret:name}</c>.</param>
/// <param name="VariableName">The environment variable that answers when the chain holds nothing.</param>
public sealed record SecretName(string Name, string VariableName)
{
    /// <summary>Gets the name the resolver chain is asked for.</summary>
    public string Name { get; } = Required(Name, nameof(Name));

    /// <summary>Gets the environment variable read when the chain holds nothing.</summary>
    public string VariableName { get; } = Required(VariableName, nameof(VariableName));

    /// <summary>Writes both names, because neither one is a value.</summary>
    /// <returns>The text <c>name (VARIABLE)</c>.</returns>
    public override string ToString() => Name + " (" + VariableName + ")";

    /// <summary>Refuses a half that names nothing.</summary>
    /// <param name="value">The name given.</param>
    /// <param name="parameter">Which half it is, for the exception to report.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentException">The name is null, empty, or blank.</exception>
    private static string Required(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        return value;
    }
}

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// One <c>${secret:name}</c> reference inside a configuration string.
/// </summary>
/// <remarks>
/// The parser reads the reference and never resolves it. <c>ISecretResolverPort</c> resolves it at
/// startup, through the four-step chain in D22.
/// </remarks>
/// <param name="Name">The secret name between the colon and the closing brace.</param>
/// <param name="Start">The position of the <c>$</c> inside the source string.</param>
/// <param name="Length">The number of characters the whole reference spans.</param>
public sealed record SecretReference(string Name, int Start, int Length)
{
    /// <summary>The text that opens a reference.</summary>
    public const string Prefix = "${secret:";

    /// <summary>Writes the reference back in its source form.</summary>
    /// <returns>The text <c>${secret:name}</c>.</returns>
    public override string ToString() => Prefix + Name + "}";
}

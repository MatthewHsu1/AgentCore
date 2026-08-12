namespace AgentCore.Application.Secrets;

/// <summary>
/// Reads the value behind one <c>${secret:name}</c> reference.
/// </summary>
/// <remarks>
/// <para>
/// The parser keeps a reference and never a value, so something has to turn the name into the value.
/// This port does that, and AgentCore calls it at startup and not on a tool call:
/// <see cref="ResolvedSecrets.ResolveAsync"/> walks the loaded document once, resolves every name it
/// finds, and hands the values to the tool factory. A tool call reads
/// <see cref="ResolvedSecrets"/> and never this port.
/// </para>
/// <para>
/// An adapter answers <see langword="null"/> when it does not hold the name, so a chain can try the
/// next link. It may still throw when it holds the name and cannot read it, because a store that is
/// present and broken is a startup failure and never a miss.
/// </para>
/// <para>
/// The value is a secret. An adapter never logs it, and never writes it into an exception message.
/// </para>
/// </remarks>
public interface ISecretResolverPort
{
    /// <summary>Reads the value of one secret.</summary>
    /// <param name="name">The name between the colon and the closing brace.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/> when this resolver does not hold the name.</returns>
    ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default);
}

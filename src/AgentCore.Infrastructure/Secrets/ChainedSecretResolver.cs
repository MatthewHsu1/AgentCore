using AgentCore.Application.Secrets;

namespace AgentCore.Infrastructure.Secrets;

/// <summary>
/// Asks each resolver in order, and takes the first hit.
/// </summary>
/// <remarks>
/// <para>
/// The host lists the links it wants and the order it wants them in. A link that does not hold the
/// name answers <see langword="null"/>, so the next link reads. The first value wins, and no later
/// link is asked. When no link holds the name, the chain answers <see langword="null"/> and
/// <see cref="ResolvedSecrets.ResolveAsync"/> turns that into the one startup failure.
/// </para>
/// <para>
/// The chain is open: a vault, a parameter store, or a test double joins by taking a place in the
/// list. Nothing here fixes the number of links, and no link knows about any other.
/// </para>
/// <para>
/// A typical order runs the most specific store first and the most general last. A host that mounts
/// its credentials as files usually writes <c>[ FileSecretResolver, EnvironmentSecretResolver ]</c>,
/// so an operator overrides one name by writing one file.
/// </para>
/// </remarks>
public sealed class ChainedSecretResolver : ISecretResolverPort
{
    private readonly ISecretResolverPort[] _links;

    /// <summary>Creates the chain.</summary>
    /// <param name="links">The resolvers, in the order the chain asks them.</param>
    public ChainedSecretResolver(IEnumerable<ISecretResolverPort> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        _links = [.. links];
        foreach (var link in _links)
        {
            ArgumentNullException.ThrowIfNull(link, nameof(links));
        }
    }

    /// <summary>Gets the number of links.</summary>
    public int Count => _links.Length;

    /// <summary>Reads the value of one secret from the first link that holds it.</summary>
    /// <param name="name">The name the document wrote.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/> when no link holds the name.</returns>
    public async ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (var link in _links)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await link.TryResolveAsync(name, cancellationToken).ConfigureAwait(false) is { } value)
            {
                return value;
            }
        }

        return null;
    }
}

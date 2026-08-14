using AgentCore.Application.Ports;

namespace AgentCore.Application.Secrets;

/// <summary>
/// The one way a vendor adapter reads a credential it cannot start without.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISecretResolverPort"/> answers <see langword="null"/> for a name it does not hold,
/// because a chain has to be able to try the next link. An adapter that needs a key therefore has a
/// second question to answer: what to do with the miss. Every adapter answered it the same way, in
/// its own copy of the same three steps, and the copies drifted in their wording while agreeing on
/// their behaviour. This extension is that answer, written once.
/// </para>
/// <para>
/// The steps are: ask the chain by <see cref="SecretName.Name"/> when a chain was bound, then read
/// the <see cref="SecretName.VariableName"/> variable, then fail. Only <see langword="null"/> moves
/// the read on to the next step. A chain that answers a blank string has answered: the name was
/// held, and the store behind it is empty. That fails here and never falls back, because a mounted
/// secret file that renders empty is a deployment defect, and reading the variable instead would
/// start the host on some other key it was never told to use.
/// </para>
/// <para>
/// A blank variable fails the same way, for the same reason: a key that is the empty string only
/// fails later, at the vendor, as a 401 that names nothing.
/// </para>
/// <para>
/// The failure is <see cref="SecretResolutionException"/>, so a missing credential is a startup
/// failure like every other configuration failure and a call never discovers one mid-turn. The
/// message names both halves, because the reader has two places to put a key and needs to be told
/// both. It never holds the value of anything, resolved or not.
/// </para>
/// </remarks>
public static class SecretResolverExtensions
{
    /// <summary>Reads one credential through the chain, then the environment, without failing.</summary>
    /// <param name="secrets">
    /// The resolver chain, or <see langword="null"/> to read the environment alone.
    /// </param>
    /// <param name="secret">The credential, named both ways.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The value, or <see langword="null"/> when neither the chain nor the environment holds the
    /// name. An empty string is not a miss: it means a store held the name and answered nothing,
    /// which is a deployment defect the caller decides what to do about.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// This is for a credential a host may legitimately not have, where the absence selects a
    /// different path rather than failing one. <see cref="KnownSecrets.GrafanaCloudInstanceId"/> is
    /// the case that asked for it: no instance id means export to a local collector with no
    /// credential, and an instance id means the token beside it is then mandatory.
    /// </para>
    /// <para>
    /// <see cref="RequireAsync"/> is this read plus the failure, so the chain-then-environment order
    /// is written once and the two paths cannot drift.
    /// </para>
    /// </remarks>
    public static async ValueTask<string?> TryReadAsync(
        this ISecretResolverPort? secrets,
        SecretName secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string? value = null;
        if (secrets is not null)
        {
            value = await secrets.TryResolveAsync(secret.Name, cancellationToken).ConfigureAwait(false);
        }

        // Only a null moves on. A chain that answered a blank string held the name, and a held name
        // that is empty is a broken store rather than a miss: falling back would swap in a key the
        // deployment never named.
        return value ?? Environment.GetEnvironmentVariable(secret.VariableName);
    }

    /// <summary>Reads one credential through the chain, then the environment, or fails.</summary>
    /// <param name="secrets">
    /// The resolver chain, or <see langword="null"/> to read the environment alone. A host that binds
    /// no chain is an ordinary case, so this takes the null rather than making each caller test for
    /// it.
    /// </param>
    /// <param name="secret">The credential, named both ways.</param>
    /// <param name="because">
    /// One sentence saying what this caller needed the key for, written whole and ending in a full
    /// stop, or <see langword="null"/> when the name says enough. It lands between the failure and
    /// the two places to put a key, so a host that never asked for an embedding model learns why one
    /// was asking for an OpenAI key.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecretResolutionException">Neither the chain nor the environment holds it.</exception>
    public static async ValueTask<string> RequireAsync(
        this ISecretResolverPort? secrets,
        SecretName secret,
        string? because = null,
        CancellationToken cancellationToken = default)
    {
        string? value = await secrets.TryReadAsync(secret, cancellationToken).ConfigureAwait(false);

        return value is { Length: > 0 } ? value : throw Missing(secret, because);
    }

    /// <summary>Builds the failure a credential that is nowhere produces.</summary>
    /// <param name="secret">The credential, named both ways.</param>
    /// <param name="because">What the caller needed it for, or <see langword="null"/>.</param>
    /// <returns>The failure. It names the two places to put a key, and no value.</returns>
    private static SecretResolutionException Missing(SecretName secret, string? because)
    {
        var why = because is { Length: > 0 } ? " " + because : string.Empty;

        return new SecretResolutionException(
            "the secret '" + secret.Name + "' did not resolve." + why + " Bind a resolver that holds '"
            + secret.Name + "', or set the " + secret.VariableName
            + " variable. Nothing on this path holds a key of its own.");
    }
}

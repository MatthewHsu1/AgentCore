using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Step 3: resolve every <c>${secret:name}</c> once, while the host starts.</summary>
internal static class SecretsStartup
{
    /// <summary>Resolves every secret the document references.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="options">The options the host filled. It carries the resolver chain.</param>
    /// <param name="cancellationToken">Cancels the secret reads.</param>
    /// <returns>Every value the document needs, read exactly once.</returns>
    /// <exception cref="SecretResolutionException">One <c>${secret:name}</c> reference resolves to nothing.</exception>
    /// <remarks>
    /// A tool call then costs no lookup, and a missing credential stops the host rather than one turn.
    /// </remarks>
    internal static ValueTask<ResolvedSecrets> ResolveAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CancellationToken cancellationToken)
        => ResolvedSecrets.ResolveAsync(
            configuration,
            options.SecretResolver ?? NoSecretResolver.Instance,
            cancellationToken);

    /// <summary>The chain a host that bound none gets: it holds no name at all.</summary>
    /// <remarks>
    /// A document that references no secret resolves cleanly against this. A document that references
    /// one fails, and the failure names the reference and its JSON Pointer.
    /// </remarks>
    private sealed class NoSecretResolver : ISecretResolverPort
    {
        public static NoSecretResolver Instance { get; } = new();

        public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }
}

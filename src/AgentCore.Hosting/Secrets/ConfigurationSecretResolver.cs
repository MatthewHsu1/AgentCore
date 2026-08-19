using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Hosting.Secrets;

/// <summary>
/// Reads a secret from one section of the host's own configuration.
/// </summary>
internal sealed class ConfigurationSecretResolver : ISecretResolverPort
{
    /// <summary>The section a secret is read from. Nothing outside it is read at all.</summary>
    internal const string SectionKey = "AgentCore:Secrets";

    private readonly IConfiguration _configuration;

    /// <summary>Creates a resolver over one configuration root.</summary>
    /// <param name="configuration">The host's configuration.</param>
    public ConfigurationSecretResolver(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <summary>Reads the value of one secret.</summary>
    /// <param name="name">The name the document wrote.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/> when the section holds neither form.</returns>
    public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();

        if (name.Length == 0)
        {
            return ValueTask.FromResult<string?>(null);
        }

        var section = _configuration.GetSection(SectionKey);

        var value = Empty(section[name]) ?? Empty(section[EnvironmentSecretResolver.ToVariableName(name)]);

        return ValueTask.FromResult(value);
    }

    /// <summary>Reads an empty setting as an unset one, so the chain goes on.</summary>
    private static string? Empty(string? value) => value is { Length: > 0 } ? value : null;
}

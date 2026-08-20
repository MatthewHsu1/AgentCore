using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Secrets;

/// <summary>
/// Reads a secret from one file of a directory.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>/run/secrets</c> convention: one file for each name, and the file holds the value
/// and nothing else. Docker, Podman, and Kubernetes all mount a secret this way, so a deployment
/// keeps the credential out of the process environment and out of the image.
/// </para>
/// <para>
/// A trailing line break is not part of the value, because an editor writes one and a credential
/// does not hold one. A name that holds a directory separator reads nothing at all: a secret name is
/// a file name and never a path.
/// </para>
/// <para>
/// A file that is not there is a miss, so the next link of the chain answers. A file that is there
/// and does not read raises the input failure, because a store that is present and broken is a
/// startup failure and never a miss.
/// </para>
/// </remarks>
public sealed class FileSecretResolver : ISecretResolverPort
{
    /// <summary>The directory a container runtime mounts a secret into.</summary>
    public const string DefaultDirectory = "/run/secrets";

    private readonly string _directory;

    /// <summary>Creates a resolver over <see cref="DefaultDirectory"/>.</summary>
    public FileSecretResolver()
        : this(DefaultDirectory)
    {
    }

    /// <summary>Creates a resolver over one directory.</summary>
    /// <param name="directory">The directory that holds one file for each secret.</param>
    public FileSecretResolver(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    /// <summary>Gets the directory this resolver reads.</summary>
    public string Directory => _directory;

    /// <summary>Reads the value of one secret.</summary>
    /// <param name="name">The name the document wrote. It is a file name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/> when the directory holds no such file.</returns>
    public async ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsFileName(name))
        {
            return null;
        }

        var path = Path.Combine(_directory, name);
        if (!File.Exists(path))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return text.TrimEnd('\r', '\n');
    }

    /// <summary>Reports whether one secret name names a file inside the directory and nothing else.</summary>
    private static bool IsFileName(string name)
        => name.Length > 0
           && name.IndexOfAny(['/', '\\']) < 0
           && !name.Contains("..", StringComparison.Ordinal)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

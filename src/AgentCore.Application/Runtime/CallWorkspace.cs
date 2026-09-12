using AgentCore.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The folder one call owns on disk: created with the call, and deleted when it ends.
/// </summary>
internal sealed class CallWorkspace
{
    private CallWorkspace(string path) => Path = path;

    /// <summary>Gets the folder's path.</summary>
    public string Path { get; }

    /// <summary>
    /// Creates (or, for a resumed call, reuses) the folder <c>root/callId</c>.
    /// </summary>
    /// <param name="root">The root a host bound.</param>
    /// <param name="callId">
    /// The call's id. A host-given id must not escape <paramref name="root"/>: the combined path is
    /// checked, not the text of the id, so this also refuses a <c>callId</c> of <c>"."</c>, which
    /// would otherwise resolve straight back to the root.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="callId"/> is empty or white space, or resolves to a path that is not a strict
    /// child of <paramref name="root"/>.
    /// </exception>
    public static CallWorkspace Create(string root, string callId)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);

        if (callId.Contains('/', StringComparison.Ordinal) || callId.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The call id '{callId}' must not contain a directory separator.", nameof(callId));
        }

        var fullRoot = System.IO.Path.GetFullPath(root);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, callId));

        var rootWithSeparator = fullRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + System.IO.Path.DirectorySeparatorChar;

        // GetFullPath can normalise the last segment away from what the caller wrote — on Windows it
        // strips trailing spaces and periods, so " ." or "call-1." would otherwise alias the root or
        // an unrelated sibling. Requiring the resolved folder name to still equal callId catches that,
        // on top of the plain containment check.
        if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal)
            || path.Length <= rootWithSeparator.Length
            || !string.Equals(System.IO.Path.GetFileName(path), callId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The call id '{callId}' must resolve to a folder under the workspace root.", nameof(callId));
        }

        Directory.CreateDirectory(path);
        return new CallWorkspace(path);
    }

    /// <summary>
    /// Deletes the folder recursively. Safe to call twice: the second call finds nothing and does
    /// not log. Never throws out of a caller ending a call.
    /// </summary>
    /// <param name="logger">Where a failed delete is logged, at Warning. May be <see langword="null"/>.</param>
    public void Delete(ILogger? logger)
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone: this is the second call, or the host removed it out of band.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A concurrent delete from outside this call may have removed the folder in the window
            // between the failing operation and this check; that is not a failure worth logging.
            if (logger is not null && Directory.Exists(Path))
            {
                Log.WorkspaceDeleteFailed(logger, Path, exception);
            }
        }
    }
}

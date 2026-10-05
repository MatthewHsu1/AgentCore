using AgentCore.Application.Diagnostics;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Sessions.Memory
{
    /// <summary>
    /// The boot sweep <see cref="InMemoryConversationSessions"/> runs once, at startup, over its workspace root.
    /// </summary>
    internal sealed class WorkspaceRootSweeper
    {
        private readonly TimeSpan _idleTimeout;

        private readonly TimeProvider _time;

        /// <summary>Creates the sweeper over one owner's idle timeout and clock.</summary>
        /// <param name="idleTimeout">The owner's idle timeout: how old a folder or a lone marker must be to sweep.</param>
        /// <param name="time">The clock the owner's idle timers run on.</param>
        internal WorkspaceRootSweeper(TimeSpan idleTimeout, TimeProvider time)
        {
            _idleTimeout = idleTimeout;
            _time = time;
        }

        /// <summary>
        /// Deletes every folder directly under <paramref name="root"/> that has a sibling marker
        /// (<see cref="ConversationWorkspace.MarkerPathFor"/>) and whose last-write time is at least the idle
        /// timeout old: a folder a crashed process left behind. The marker goes with it. A folder without a
        /// marker is never a folder <see cref="ConversationWorkspace.Create"/> made, so it is left alone
        /// untouched, whatever its stamp — a workspace root that happens to point at a shared or pre-existing
        /// folder must lose nothing this library did not put there. A marker whose folder is already gone is a
        /// leftover of its own — from a folder deleted out of band, say — and is deleted once its own stamp is
        /// old enough, by the same cutoff. Meant to run once, at boot, before the owner has opened any session
        /// of its own; a folder the owner already holds by the time this runs is skipped regardless of its
        /// stamp, so a caller that runs it later never sweeps a live session. A folder that is a symbolic link
        /// is skipped untouched, so this never follows a link out of the root. Never throws: a folder, a
        /// marker, or the root itself, that cannot be listed, inspected, or deleted is logged and left for the
        /// next boot.
        /// </summary>
        /// <param name="root">The workspace root a host bound.</param>
        /// <param name="isLive">Answers whether a conversation id is already held live by the calling owner.</param>
        /// <param name="logger">Where a failed list or delete is logged, at Warning. May be <see langword="null"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="root"/> is empty.</exception>
        internal void Sweep(string root, Func<string, bool> isLive, ILogger? logger)
        {
            ArgumentException.ThrowIfNullOrEmpty(root);

            string[] folders;
            string[] markers;
            try
            {
                // Eager, not EnumerateDirectories/EnumerateFiles: a lazy sequence would run its own OS-level
                // listing calls outside this try, on whatever caller code enumerates it, so a fault partway
                // through a large or unreachable folder would escape uncaught instead of being logged here.
                folders = Directory.GetDirectories(root);
                markers = Directory.GetFiles(root, "*" + ConversationWorkspace.MarkerFileName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                if (logger is not null)
                {
                    SessionOwnerLog.WorkspaceSweepListFailed(logger, root, exception);
                }

                return;
            }

            // No margin for clock skew: a root shared by several servers is the host's to coordinate.
            DateTime cutoff = _time.GetUtcNow().UtcDateTime - _idleTimeout;

            HashSet<string> liveFolderNames = new(folders.Select(folder => Path.GetFileName(folder)!), StringComparer.Ordinal);

            foreach (string folder in folders)
            {
                if (isLive(Path.GetFileName(folder)!))
                {
                    continue;
                }

                try
                {
                    DirectoryInfo info = new(folder);
                    if (info.LinkTarget is not null)
                    {
                        continue;
                    }

                    string marker = ConversationWorkspace.MarkerPathFor(folder);
                    if (!File.Exists(marker))
                    {
                        continue;
                    }

                    if (info.LastWriteTimeUtc <= cutoff)
                    {
                        Directory.Delete(folder, recursive: true);
                        TryDeleteMarker(marker, logger);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                {
                    if (logger is not null)
                    {
                        SessionOwnerLog.WorkspaceSweepDeleteFailed(logger, folder, exception);
                    }
                }
            }

            foreach (string marker in markers)
            {
                string id = Path.GetFileName(marker)[..^ConversationWorkspace.MarkerFileName.Length];
                if (liveFolderNames.Contains(id))
                {
                    // Has a folder: handled above, alongside it, whichever way that went.
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(marker) <= cutoff)
                    {
                        TryDeleteMarker(marker, logger);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    if (logger is not null)
                    {
                        SessionOwnerLog.WorkspaceSweepDeleteFailed(logger, marker, exception);
                    }
                }
            }
        }

        /// <summary>Deletes one workspace marker file. A failure is logged, never thrown.</summary>
        private static void TryDeleteMarker(string marker, ILogger? logger)
        {
            try
            {
                File.Delete(marker);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (logger is not null)
                {
                    SessionOwnerLog.WorkspaceMarkerDeleteFailed(logger, marker, exception);
                }
            }
        }
    }
}

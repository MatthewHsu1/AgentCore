using AgentCore.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The folder one conversation owns on disk: created with the session, and deleted when the session closes, unloads, or ends.
    /// </summary>
    internal sealed class ConversationWorkspace
    {
        /// <summary>
        /// The suffix <see cref="Create"/> appends to a workspace's path to name its marker file, a sibling of
        /// the folder rather than a file inside it: <c>root/conversationId.agentcore-workspace</c>. The boot
        /// sweep (<see cref="Sessions.Memory.InMemoryConversationSessions.SweepWorkspaceRoot"/>) deletes a
        /// folder only when its marker is present, so a workspace root a host points at a folder used for
        /// anything else — the current directory, a home directory, a shared volume — never loses a folder
        /// AgentCore did not make. Kept outside the folder because a model can read, list, and write inside a
        /// workspace: a marker sitting in there would show up in a file listing or a memory index, and would
        /// make the folder non-empty for a tool that expects one, such as a bare <c>git clone</c>.
        /// </summary>
        internal const string MarkerFileName = ".agentcore-workspace";

        private ConversationWorkspace(string path)
        {
            Path = path;
        }

        /// <summary>Gets the folder's path.</summary>
        public string Path { get; }

        /// <summary>Gets the path of this workspace's marker file: <see cref="Path"/> plus <see cref="MarkerFileName"/>.</summary>
        internal string MarkerPath => MarkerPathFor(Path);

        /// <summary>Gets the marker path a folder at <paramref name="workspacePath"/> would have.</summary>
        internal static string MarkerPathFor(string workspacePath)
        {
            return workspacePath + MarkerFileName;
        }

        /// <summary>
        /// Creates (or, for a resumed conversation, reuses) the folder <c>root/conversationId</c>: writes its
        /// sibling marker the boot sweep looks for, and stamps the folder with now, so a reused folder is never
        /// mistaken for one a crashed process left behind before its first turn even runs.
        /// </summary>
        /// <param name="root">The root a host bound.</param>
        /// <param name="conversationId">
        /// The conversation's id. A host-given id must not escape <paramref name="root"/>: the combined path is
        /// checked, not the text of the id, so this also refuses a <c>conversationId</c> of <c>"."</c>, which
        /// would otherwise resolve straight back to the root.
        /// </param>
        /// <param name="time">The clock to stamp with. Defaults to the system clock.</param>
        /// <param name="logger">Where a failed marker write or stamp is logged, at Warning. May be <see langword="null"/>.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="conversationId"/> is empty or white space, or resolves to a path that is not a strict
        /// child of <paramref name="root"/>.
        /// </exception>
        public static ConversationWorkspace Create(
            string root, string conversationId, TimeProvider? time = null, ILogger? logger = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(root);
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

            if (conversationId.Contains('/', StringComparison.Ordinal) || conversationId.Contains('\\', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The conversation id '{conversationId}' must not contain a directory separator.", nameof(conversationId));
            }

            string fullRoot = System.IO.Path.GetFullPath(root);
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, conversationId));

            string rootWithSeparator = fullRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + System.IO.Path.DirectorySeparatorChar;

            // GetFullPath can normalise the last segment away from what the caller wrote — on Windows it
            // strips trailing spaces and periods, so " ." or "conversation-1." would otherwise alias the root or
            // an unrelated sibling. Requiring the resolved folder name to still equal conversationId catches that,
            // on top of the plain containment check.
            if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal)
                || path.Length <= rootWithSeparator.Length
                || !string.Equals(System.IO.Path.GetFileName(path), conversationId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The conversation id '{conversationId}' must resolve to a folder under the workspace root.", nameof(conversationId));
            }

            _ = Directory.CreateDirectory(path);

            ConversationWorkspace workspace = new(path);

            // A marker that cannot be written — its path collides with something else, say — must not stop the
            // conversation from getting a working folder. It is simply never a candidate the boot sweep considers.
            string marker = workspace.MarkerPath;
            try
            {
                if (!File.Exists(marker))
                {
                    File.WriteAllText(marker, string.Empty);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (logger is not null)
                {
                    SessionOwnerLog.WorkspaceMarkerWriteFailed(logger, marker, exception);
                }
            }

            // A reused folder's mtime can be well older than the idle timeout: CreateDirectory above did not
            // touch it, since the folder already existed. Stamping here means "held" implies "stamped within
            // the idle timeout" from the very moment a caller has the workspace, not only from its first turn.
            workspace.Touch(time ?? TimeProvider.System, logger);
            return workspace;
        }

        /// <summary>
        /// Stamps the folder's last-write time with <paramref name="time"/>'s now. Called at the start of every
        /// turn, so the boot sweep on another server sharing the workspace root can tell this folder is still in
        /// active use from one a crashed process left behind (see <see cref="Sessions.Memory.InMemoryConversationSessions"/>).
        /// Never throws: a failed stamp must not fail the turn it belongs to.
        /// </summary>
        /// <param name="time">The clock to stamp with. A test passes a fake clock so the stamp is deterministic.</param>
        /// <param name="logger">Where a failed stamp is logged, at Warning. May be <see langword="null"/>.</param>
        public void Touch(TimeProvider time, ILogger? logger)
        {
            ArgumentNullException.ThrowIfNull(time);

            try
            {
                Directory.SetLastWriteTimeUtc(Path, time.GetUtcNow().UtcDateTime);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A close racing this stamp, or an idle poll racing another touch, can delete the folder in the
                // window between the failing call and this check: that is the ordinary outcome of the race, not
                // a fault worth logging. The runtime throws FileNotFoundException here, not
                // DirectoryNotFoundException, so this checks what is actually on disk rather than one exception type.
                if (logger is not null && Directory.Exists(Path))
                {
                    SessionOwnerLog.WorkspaceStampFailed(logger, Path, exception);
                }
            }
        }

        /// <summary>
        /// Deletes the folder recursively, and its sibling marker. Safe to call twice: the second call finds
        /// nothing and does not log. Never throws out of a caller ending a conversation.
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
                // A concurrent delete from outside this conversation may have removed the folder in the window
                // between the failing operation and this check; that is not a failure worth logging.
                if (logger is not null && Directory.Exists(Path))
                {
                    SessionOwnerLog.WorkspaceDeleteFailed(logger, Path, exception);
                }
            }

            string marker = MarkerPath;
            try
            {
                File.Delete(marker);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (logger is not null && File.Exists(marker))
                {
                    SessionOwnerLog.WorkspaceMarkerDeleteFailed(logger, marker, exception);
                }
            }
        }
    }
}

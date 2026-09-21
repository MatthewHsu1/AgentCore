using AgentCore.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The folder one conversation owns on disk: created with the conversation, and deleted when it ends.
    /// </summary>
    internal sealed class ConversationWorkspace
    {
        private ConversationWorkspace(string path)
        {
            Path = path;
        }

        /// <summary>Gets the folder's path.</summary>
        public string Path { get; }

        /// <summary>
        /// Creates (or, for a resumed conversation, reuses) the folder <c>root/conversationId</c>.
        /// </summary>
        /// <param name="root">The root a host bound.</param>
        /// <param name="conversationId">
        /// The conversation's id. A host-given id must not escape <paramref name="root"/>: the combined path is
        /// checked, not the text of the id, so this also refuses a <c>conversationId</c> of <c>"."</c>, which
        /// would otherwise resolve straight back to the root.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="conversationId"/> is empty or white space, or resolves to a path that is not a strict
        /// child of <paramref name="root"/>.
        /// </exception>
        public static ConversationWorkspace Create(string root, string conversationId)
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
            return new ConversationWorkspace(path);
        }

        /// <summary>
        /// Deletes the folder recursively. Safe to call twice: the second call finds nothing and does
        /// not log. Never throws out of a caller ending a conversation.
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
                    Log.WorkspaceDeleteFailed(logger, Path, exception);
                }
            }
        }
    }
}
